#nullable enable

// T-W2.4-01 [Edit] tests: local:T-W2.4-01:undo-scope — SPEC §G3 test 1: after a
// three-node Run, three Undo steps return the Scene to its pre-run state, each
// Undo reverting exactly one step's changes, and every step's group carries the
// SPEC §E4 step-5 name `OpWeave: <workflowId>/<nodeId>` even though no
// Operation opens a group of its own (SPEC §G3 item 1).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class UndoScopeTests
    {
        private const string WorkflowId = "example.undoscope";
        private const string RootName = "OpWeaveUndoProbe";
        private const string NodeA = "a";
        private const string NodeB = "b";
        private const string NodeC = "c";
        private const string OpA = "test.undo.edit_a";
        private const string OpB = "test.undo.edit_b";
        private const string OpC = "test.undo.edit_c";

        private string _root = null!;
        private GameObject _probe = null!;
        private SceneSetup[]? _savedSetup;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveUndoScopeTests",
                "t" + Guid.NewGuid().ToString("N"));

            // The steps change a Scene, so the test owns one: the current setup
            // is saved and replaced by an empty single scene, restored in
            // TearDown (the StepContextIoTests pattern). Nothing is written
            // under Assets/ — the scene stays unsaved, which Undo handles.
            _savedSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _probe = new GameObject(RootName);
            _objects.Add(_probe);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            foreach (var go in _objects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            _objects.Clear();

            // The three step groups this test undid now sit on the redo stack;
            // dropping them keeps them from replaying against the objects just
            // destroyed, and from leaking into the next test class's buffer.
            Undo.ClearAll();

            if (_savedSetup is not null && _savedSetup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(_savedSetup);
                _savedSetup = null;
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

        // ── SPEC §G3 test 1: three-node Run, then three Undo steps ─────────

        [Test]
        public void ThreeNodeRunIsRestoredByThreeUndoSteps()
        {
            var fixture = NewFixture();
            var ops = RegisterThreeEditOperations(fixture);

            var before = Snapshot();
            var runId = Run(fixture);

            // The run really did change the Scene, and each step left its own
            // state behind — otherwise the Undo assertions below could pass on
            // a Scene nothing ever touched.
            Assert.That(Snapshot(), Is.EqualTo(ops[2].SceneAfterChanges),
                "the Scene at the end of the Run is the last step's state");
            Assert.That(ops[0].SceneAfterChanges, Is.Not.EqualTo(before));
            Assert.That(ops[1].SceneAfterChanges, Is.Not.EqualTo(ops[0].SceneAfterChanges));
            Assert.That(ops[2].SceneAfterChanges, Is.Not.EqualTo(ops[1].SceneAfterChanges));

            // One Undo per step: the first reverts node "c" alone, the second
            // node "b", and the third node "a" — which is the pre-run Scene
            // (SPEC §G3 item 1: 1 step = 1 Undo).
            Undo.PerformUndo();
            Assert.That(Snapshot(), Is.EqualTo(ops[1].SceneAfterChanges),
                "the 1st Undo must revert exactly node 'c'");
            Undo.PerformUndo();
            Assert.That(Snapshot(), Is.EqualTo(ops[0].SceneAfterChanges),
                "the 2nd Undo must revert exactly node 'b'");
            Undo.PerformUndo();
            Assert.That(Snapshot(), Is.EqualTo(before),
                "three Undos must return the Scene to its pre-run state");

            // G3's other duty: the step's Scene changes reach the §F7 change
            // list, and a Scene change is recorded as undoable.
            foreach (var nodeId in new[] { NodeA, NodeB, NodeC })
            {
                Assert.That(
                    RunnerHostFixture.JournalHasAll(
                        _root, runId,
                        "\"type\":\"step_succeeded\"",
                        "\"nodeId\":\"" + nodeId + "\"",
                        "\"action\":\"created\"",
                        "\"undoable\":true"),
                    Is.True,
                    "node " + nodeId + " journaled no undoable created change");
            }
        }

        // ── The §E4 step-5 scope: one group per step, named, none by the
        //    Operation itself ───────────────────────────────────────────────

        [Test]
        public void RunnerOpensOneNamedUndoGroupPerStep()
        {
            var fixture = NewFixture();
            var ops = RegisterThreeEditOperations(fixture);

            Run(fixture);

            for (var i = 0; i < ops.Length; i++)
            {
                var nodeId = new[] { NodeA, NodeB, NodeC }[i];
                Assert.AreEqual(
                    "OpWeave: " + WorkflowId + "/" + nodeId,
                    ops[i].GroupNameAtEntry,
                    "the step's Undo group must carry the SPEC §E4 step-5 name");
            }

            // The Operations never open a group: the group index the scope
            // incremented into is still the current one after each step
            // registered all of its changes, and each step got its own group.
            foreach (var op in ops)
            {
                Assert.AreEqual(
                    op.GroupIndexAtEntry, op.GroupIndexAfterChanges,
                    "an Operation must not open or close an Undo group (SPEC §G3 item 1)");
            }
            Assert.Greater(ops[1].GroupIndexAtEntry, ops[0].GroupIndexAtEntry);
            Assert.Greater(ops[2].GroupIndexAtEntry, ops[1].GroupIndexAtEntry);
        }

        // ── Fixture wiring ──────────────────────────────────────────────────

        private SceneEditOperation[] RegisterThreeEditOperations(RunnerHostFixture fixture)
        {
            var ops = new[]
            {
                new SceneEditOperation(OpA, _probe),
                new SceneEditOperation(OpB, _probe),
                new SceneEditOperation(OpC, _probe),
            };
            fixture.Register(ops[0], 'a');
            fixture.Register(ops[1], 'c');
            fixture.Register(ops[2], 'e');
            return ops;
        }

        // Accepts and drives the three-node workflow (a → b → c) to Succeeded
        // and returns the Run id.
        private string Run(RunnerHostFixture fixture)
        {
            var plan = RunnerHostFixture.Plan(WorkflowId, new[]
            {
                Step(0, NodeA, OpA, 'a', next: 1),
                Step(1, NodeB, OpB, 'c', next: 2),
                Step(2, NodeC, OpC, 'e', next: -1),
            });
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = NodeA, Operation = OpA, Version = 1 },
                    new NodeData { Id = NodeB, Operation = OpB, Version = 1 },
                    new NodeData { Id = NodeC, Operation = OpC, Version = 1 },
                },
                new[]
                {
                    new ControlEdge(NodeA, NodeB),
                    new ControlEdge(NodeB, NodeC),
                });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;

            // One editor-update slice at a time, as the production
            // EditorApplication.update subscription does; the event itself is
            // never invoked from a test (the UTF callback would re-enter).
            for (var i = 0; i < 50; i++)
            {
                var state = fixture.Host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    Assert.AreEqual(RunState.Succeeded, state, "the Run did not succeed");
                    return runId;
                }
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not reach a terminal state within 50 ticks.");
            return runId;
        }

        // The Plan steps carry the Descriptor's declared scene.modify effect;
        // the fixture's idempotency/fingerprint defaults are unchanged.
        private static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed, int next)
        {
            var step = RunnerHostFixture.Step(
                index,
                nodeId,
                operationId,
                hashSeed,
                outputTypes: new Dictionary<string, string> { ["child"] = "core.String" },
                next: next,
                idempotency: Idempotency.Unsafe);
            return step with { Effects = new[] { EffectNames.SceneModify } };
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

        // ── Scene state snapshot ────────────────────────────────────────────

        // A canonical text view of the Scene hierarchy: every GameObject with
        // its transform values, active flag and component list. Every Undo
        // assertion compares one snapshot with another, so what is proven is
        // that the restored Scene is byte-for-byte the state being compared.
        private List<string> Snapshot() => SnapshotOf(_probe.scene);

        private static List<string> SnapshotOf(Scene scene)
        {
            var lines = new List<string>();
            foreach (var root in scene.GetRootGameObjects())
            {
                AppendSnapshot(lines, root, root.name);
            }
            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        private static void AppendSnapshot(List<string> lines, GameObject go, string path)
        {
            var t = go.transform;
            var line = new StringBuilder()
                .Append(path)
                .Append("|pos=").Append(Fmt(t.position))
                .Append("|rot=").Append(Fmt(t.localEulerAngles))
                .Append("|scale=").Append(Fmt(t.localScale))
                .Append("|active=").Append(go.activeSelf ? 1 : 0);
            foreach (var component in t.GetComponents<Component>())
            {
                line.Append("|comp=").Append(component.GetType().FullName);
            }
            lines.Add(line.ToString());
            for (var i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                AppendSnapshot(lines, child.gameObject, path + "/" + child.name);
            }
        }

        private static string Fmt(Vector3 value) =>
            string.Format(
                CultureInfo.InvariantCulture,
                "({0:R}, {1:R}, {2:R})",
                value.x,
                value.y,
                value.z);

        /// <summary>
        /// A scene-modifying Immediate Operation written the way SPEC §G3 asks
        /// an Operation to be written: it declares <c>scene.modify</c> with
        /// <c>Undo = Full</c>, makes every Scene change through an Undo API,
        /// records the change for SPEC §F7 and opens no Undo group of its own —
        /// the Runner's §E4 step-5 scope owns it.
        /// </summary>
        internal sealed class SceneEditOperation : OperationBase
        {
            private readonly GameObject _root;
            private readonly string _childName;

            internal SceneEditOperation(string id, GameObject root)
            {
                _root = root;
                _childName = "child-" + id;
                Descriptor = Op.Define(id, 1)
                    .Summary("Changes the Scene through the Undo APIs only.")
                    .Output("child", "core.String")
                    .Effects(EffectNames.SceneModify)
                    .Undo(UndoSupport.Full)
                    .Idempotency(Idempotency.Unsafe)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            /// <summary>The group name the Runner's scope had just set.</summary>
            internal string GroupNameAtEntry { get; private set; } = "";

            /// <summary>The group index when the step started.</summary>
            internal int GroupIndexAtEntry { get; private set; }

            /// <summary>The group index after the step's changes were registered.</summary>
            internal int GroupIndexAfterChanges { get; private set; }

            /// <summary>The Scene snapshot this step left behind.</summary>
            internal List<string> SceneAfterChanges { get; private set; } = new List<string>();

            public override StepResult Execute(StepContext ctx)
            {
                GroupNameAtEntry = Undo.GetCurrentGroupName();
                GroupIndexAtEntry = Undo.GetCurrentGroup();

                var child = new GameObject(_childName);
                Undo.RegisterCreatedObjectUndo(child, "Create " + _childName);
                Undo.SetTransformParent(child.transform, _root.transform, "Parent " + _childName);

                Undo.RecordObject(_root.transform, "Move " + _root.name);
                var position = _root.transform.position;
                _root.transform.position = new Vector3(
                    position.x + 1f, position.y, position.z);

                Undo.AddComponent(_root, typeof(MeshFilter));

                ctx.Changes.Created(
                    new ChangeTarget("gameObject", HierarchyPath(child)), undoable: true);
                ctx.Changes.Modified(
                    new ChangeTarget("gameObject", HierarchyPath(_root)), undoable: true);

                GroupIndexAfterChanges = Undo.GetCurrentGroup();
                SceneAfterChanges = SnapshotOf(_root.scene);
                ctx.SetOutput("child", _childName);
                return StepResult.Succeeded();
            }

            private static string HierarchyPath(GameObject go)
            {
                var path = go.name;
                for (var parent = go.transform.parent; parent != null; parent = parent.parent)
                {
                    path = parent.name + "/" + path;
                }
                return path;
            }
        }
    }
}
