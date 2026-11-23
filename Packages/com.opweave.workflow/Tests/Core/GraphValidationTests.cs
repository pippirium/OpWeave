#nullable enable

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §D5 graph validation phase
    /// (local:T-W1.6-04:graph-rules): the generic control graph plus the
    /// <see cref="V1ControlFlowRules"/> constraint class produce OW-VAL-001 for a
    /// missing/extra/entered <c>core.start</c>, OW-VAL-004 for branching, joins
    /// and non-"next" control ports (with the SPEC §N.3 item 3 "branching
    /// unsupported in this version" hint), OW-VAL-003 for cycles, OW-VAL-002 for
    /// unreachable nodes, OW-VAL-007 for data suppliers that are not guaranteed
    /// to run before their consumer, OW-VAL-010 over 200 nodes, and the execution
    /// order for a linear workflow (SPEC §D5 test, §N.1). The same sources run
    /// under dotnet and Unity Mono.
    /// </summary>
    internal sealed class GraphValidationTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────

        private static NodeData Node(string id, string operation = "example.operation") =>
            new NodeData { Id = id, Operation = operation, Version = 1 };

        private static WorkflowDocumentData Data(
            IEnumerable<NodeData> nodes,
            IEnumerable<ControlEdge> controlEdges,
            IEnumerable<DataEdge>? dataEdges = null) => new WorkflowDocumentData
        {
            SchemaVersion = "1.0",
            Id = "example.graph",
            Nodes = new List<NodeData>(nodes),
            ControlEdges = new List<ControlEdge>(controlEdges),
            DataEdges = dataEdges is null ? null : new List<DataEdge>(dataEdges),
        };

        private static WorkflowDocumentData LinearData() => Data(
            new[] { Node("start", "core.start"), Node("a"), Node("b") },
            new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") });

        private static WorkflowDocument MustCreate(WorkflowDocumentData data)
        {
            var result = WorkflowDocument.Create(data);
            Assert.IsTrue(result.Ok, "Create failed: " + string.Join(
                "; ", result.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return result.Value!;
        }

        private static ValidationReport Compile(WorkflowDocumentData data) =>
            new WorkflowCompiler().Validate(new ValidationRequest(
                MustCreate(data), null, ValidationMode.Static, ActorKind.Agent));

        // Runs the real D5 phase directly so graph findings are observable without
        // a clean D2 (dangling references are the D2 phase's OW-VAL-011 finding)
        // and so the phase's ExecutionOrder output can be asserted.
        private static ValidationContext RunPhase(WorkflowDocumentData data)
        {
            var context = new ValidationContext(new ValidationRequest(
                MustCreate(data), null, ValidationMode.Static, ActorKind.Agent));
            new GraphValidationPhase().Run(context);
            return context;
        }

        private static List<Diagnostic> NewDiagnostics(ValidationContext context, int skip = 0) =>
            context.Diagnostics.Skip(skip).ToList();

        // ── Linear flow and execution order (SPEC §D5 test, §D7 item 1) ─────────

        [Test]
        public void LinearWorkflowPassesAndYieldsExecutionOrder()
        {
            var report = Compile(LinearData());
            Assert.IsTrue(report.Ok, string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));

            var context = RunPhase(LinearData());
            Assert.IsEmpty(context.Diagnostics);
            Assert.IsNotNull(context.ExecutionOrder);
            Assert.AreEqual(
                new[] { "start", "a", "b" },
                context.ExecutionOrder!.Select(n => n.Id).ToArray());
        }

        [Test]
        public void DocumentOrderIsNotTheExecutionOrder()
        {
            // Nodes are declared out of chain order; the execution order follows
            // the control edges, not the declaration order.
            var data = Data(
                new[] { Node("start", "core.start"), Node("b"), Node("a") },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") });
            var context = RunPhase(data);
            Assert.IsEmpty(context.Diagnostics);
            Assert.AreEqual(
                new[] { "start", "a", "b" },
                context.ExecutionOrder!.Select(n => n.Id).ToArray());
        }

        [Test]
        public void InvalidGraphLeavesNoExecutionOrder()
        {
            var context = RunPhase(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b"),
                    new ControlEdge("b", "a"),
                }));
            Assert.IsNull(context.ExecutionOrder);
        }

        // ── core.start (SPEC §D5 item 1, OW-VAL-001) ────────────────────────────

        [Test]
        public void TwoStartsReportOwVal001()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("second", "core.start"), Node("a") },
                new[] { new ControlEdge("start", "a") }));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_001, diagnostic.Code);
            Assert.AreEqual("second", diagnostic.NodeId);
            Assert.AreEqual("$.nodes[1].operation", diagnostic.JsonPath);
        }

        [Test]
        public void MissingStartReportsOwVal001WithoutUnreachableCascade()
        {
            var report = Compile(Data(
                new[] { Node("a"), Node("b") },
                new[] { new ControlEdge("a", "b") }));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_001, diagnostic.Code);
            Assert.IsNull(diagnostic.NodeId);
        }

        [Test]
        public void StartWithIncomingEdgeReportsOwVal001()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("x"), Node("a") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("x", "start"),
                }));
            var entry = report.Errors.Single(d => d.Code == ErrorCodes.OW_VAL_001);
            Assert.AreEqual("start", entry.NodeId);
            Assert.AreEqual("$.controlEdges[1]", entry.JsonPath);
            // 'x' sits before the entry point; the walk from start never reaches it.
            Assert.IsTrue(report.Errors.Any(d =>
                d.Code == ErrorCodes.OW_VAL_002 && d.NodeId == "x"));
        }

        // ── Reachability and cycles (SPEC §D5 item 4, OW-VAL-002/003) ──────────

        [Test]
        public void IsolatedNodeReportsOwVal002()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("stray") },
                new[] { new ControlEdge("start", "a") }));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_002, diagnostic.Code);
            Assert.AreEqual("stray", diagnostic.NodeId);
            Assert.AreEqual("$.nodes[2]", diagnostic.JsonPath);
        }

        [Test]
        public void CycleReportsOwVal003()
        {
            // A→B→A: the edge from 'b' returns to 'a', already on the path.
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b"),
                    new ControlEdge("b", "a"),
                }));
            var cycle = report.Errors.Single(d => d.Code == ErrorCodes.OW_VAL_003);
            Assert.AreEqual("b", cycle.NodeId);
            Assert.AreEqual("$.controlEdges[2]", cycle.JsonPath);
            Assert.IsFalse(report.Ok);
        }

        [Test]
        public void SelfLoopReportsOwVal003()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a") },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "a") }));
            var diagnostic = report.Errors.Single(d => d.Code == ErrorCodes.OW_VAL_003);
            Assert.AreEqual("a", diagnostic.NodeId);
        }

        [Test]
        public void UnreachableCycleReportsOwVal002NotOwVal003()
        {
            // SPEC §D5 item 4 walks from start only: a cycle the walk never
            // reaches is an unreachable finding, not a cycle finding.
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b"), Node("c") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("b", "c"),
                    new ControlEdge("c", "b"),
                }));
            var codes = report.Errors.Select(d => d.Code).Distinct().ToArray();
            Assert.AreEqual(new[] { ErrorCodes.OW_VAL_002 }, codes);
            Assert.AreEqual(2, report.Errors.Count);
        }

        // ── Ports and degrees (SPEC §D5 items 2-3, OW-VAL-004) ─────────────────

        [Test]
        public void BranchReportsOwVal004()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b"), Node("c") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b"),
                    new ControlEdge("a", "c"),
                }));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_004, diagnostic.Code);
            Assert.AreEqual("a", diagnostic.NodeId);
            Assert.AreEqual("$.controlEdges[2]", diagnostic.JsonPath);
            StringAssert.Contains("Branching is not supported in this version",
                diagnostic.Hint ?? string.Empty);
        }

        [Test]
        public void JoinReportsOwVal004()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b"), Node("c") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b"),
                    new ControlEdge("a", "c"),
                    new ControlEdge("b", "c"),
                }));
            var join = report.Errors.Single(d =>
                d.Code == ErrorCodes.OW_VAL_004 &&
                d.JsonPath == "$.controlEdges[3]");
            Assert.AreEqual("c", join.NodeId);
            StringAssert.Contains("Merging", join.Hint ?? string.Empty);
        }

        [Test]
        public void UnknownFromPortReportsOwVal004WithBranchingHint()
        {
            // SPEC §N.3 item 3: a fromPort a newer schema could introduce (a 1.1
            // document) is rejected with the "branching unsupported" hint.
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b", "true"),
                }));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_004, diagnostic.Code);
            Assert.AreEqual("a", diagnostic.NodeId);
            Assert.AreEqual("true", diagnostic.Port);
            Assert.AreEqual("$.controlEdges[1].fromPort", diagnostic.JsonPath);
            StringAssert.Contains("Branching is not supported in this version",
                diagnostic.Hint ?? string.Empty);
        }

        [Test]
        public void ReservedFuturePortNamesAreRejectedToo()
        {
            // "body"/"done" are appendix-N ForEach ports, not valid v1 values.
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b", "body"),
                }));
            Assert.AreEqual(ErrorCodes.OW_VAL_004, report.Errors.Single().Code);
        }

        // ── Data supplier dominance (SPEC §D5 item 5, OW-VAL-007) ──────────────

        [Test]
        public void ReversedDataEdgeReportsOwVal007()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b") },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[] { new DataEdge(new PortRef("b", "out"), new PortRef("a", "in")) }));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_007, diagnostic.Code);
            Assert.AreEqual("a", diagnostic.NodeId);
            Assert.AreEqual("in", diagnostic.Port);
            Assert.AreEqual("$.dataEdges[0]", diagnostic.JsonPath);
        }

        [Test]
        public void ForwardDataEdgePasses()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b") },
                new[] { new ControlEdge("start", "a"), new ControlEdge("a", "b") },
                new[] { new DataEdge(new PortRef("a", "out"), new PortRef("b", "in")) }));
            Assert.IsTrue(report.Ok, string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
        }

        [Test]
        public void SelfDataEdgeReportsOwVal007()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a") },
                new[] { new ControlEdge("start", "a") },
                new[] { new DataEdge(new PortRef("a", "out"), new PortRef("a", "in")) }));
            Assert.AreEqual(ErrorCodes.OW_VAL_007, report.Errors.Single().Code);
        }

        [Test]
        public void UnreachableSupplierReportsOwVal007OnTheConsumer()
        {
            // The supplier never executes, so it cannot dominate the consumer.
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b") },
                new[] { new ControlEdge("start", "a") },
                new[] { new DataEdge(new PortRef("b", "out"), new PortRef("a", "in")) }));
            Assert.IsTrue(report.Errors.Any(d =>
                d.Code == ErrorCodes.OW_VAL_007 && d.NodeId == "a"));
            Assert.IsTrue(report.Errors.Any(d =>
                d.Code == ErrorCodes.OW_VAL_002 && d.NodeId == "b"));
        }

        [Test]
        public void UnreachableConsumerReportsOnlyOwVal002()
        {
            var report = Compile(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b") },
                new[] { new ControlEdge("start", "a") },
                new[] { new DataEdge(new PortRef("a", "out"), new PortRef("b", "in")) }));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_002, diagnostic.Code);
            Assert.AreEqual("b", diagnostic.NodeId);
        }

        // ── Node count (SPEC §D5 item 6, OW-VAL-010) ────────────────────────────

        private static WorkflowDocumentData ChainData(int nodeCount)
        {
            var nodes = new List<NodeData> { Node("start", "core.start") };
            var edges = new List<ControlEdge>();
            for (var i = 1; i < nodeCount; i++)
            {
                nodes.Add(Node("n" + i));
                edges.Add(new ControlEdge(i == 1 ? "start" : "n" + (i - 1), "n" + i));
            }
            return Data(nodes, edges);
        }

        [Test]
        public void Exactly200NodesPasses()
        {
            var report = Compile(ChainData(200));
            Assert.IsTrue(report.Ok, string.Join(
                "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
        }

        [Test]
        public void MoreThan200NodesReportsOwVal010()
        {
            var report = Compile(ChainData(201));
            var diagnostic = report.Errors.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_010, diagnostic.Code);
            StringAssert.Contains("201", diagnostic.Message);
        }

        // ── §D1 item 2 interaction with the graph ──────────────────────────────

        [Test]
        public void ExcludedMidChainNodeKeepsDownstreamReachable()
        {
            // Exclusion (SPEC §D1 item 2) drops a node's findings, not the
            // topology: 'b' keeps its edges so 'c' stays reachable and the order
            // still walks through the chain.
            var data = Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b"), Node("c") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b"),
                    new ControlEdge("b", "c"),
                });
            var context = new ValidationContext(new ValidationRequest(
                MustCreate(data), null, ValidationMode.Static, ActorKind.Agent));
            context.Report(ErrorCodes.Create(
                ErrorCodes.OW_OP_001, "Prior phase failure.", nodeId: "b"));

            new GraphValidationPhase().Run(context);

            Assert.IsEmpty(NewDiagnostics(context, skip: 1));
            Assert.AreEqual(
                new[] { "start", "a", "b", "c" },
                context.ExecutionOrder!.Select(n => n.Id).ToArray());
        }

        [Test]
        public void ExcludedNodeFindingsAreSuppressed()
        {
            var data = Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b"), Node("c") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "b"),
                    new ControlEdge("a", "c"),
                });
            var context = new ValidationContext(new ValidationRequest(
                MustCreate(data), null, ValidationMode.Static, ActorKind.Agent));
            context.Report(ErrorCodes.Create(
                ErrorCodes.OW_OP_001, "Prior phase failure.", nodeId: "a"));

            new GraphValidationPhase().Run(context);

            Assert.IsFalse(context.Diagnostics.Any(d =>
                d.Code == ErrorCodes.OW_VAL_004 && d.NodeId == "a"));
        }

        [Test]
        public void DanglingControlEdgeIsNotAGraphFinding()
        {
            // Reference integrity is D2's OW-VAL-011; the phase must not
            // re-diagnose it — 'b' is simply unreachable.
            var context = RunPhase(Data(
                new[] { Node("start", "core.start"), Node("a"), Node("b") },
                new[]
                {
                    new ControlEdge("start", "a"),
                    new ControlEdge("a", "ghost"),
                }));
            var diagnostic = context.Diagnostics.Single();
            Assert.AreEqual(ErrorCodes.OW_VAL_002, diagnostic.Code);
            Assert.AreEqual("b", diagnostic.NodeId);
        }
    }
}
