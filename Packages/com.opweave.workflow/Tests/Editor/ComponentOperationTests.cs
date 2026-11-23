#nullable enable

// T-W2.5-03 [Edit] tests: local:T-W2.5-03:component-ops — the G5 triad
// (success, single Undo, precondition violation) for the two §I.1
// Component Operations through a real UnityStepContext over the standard
// type registry: unity.component.add (TypeCache-derived resolution,
// Component-derived non-abstract check, DisallowMultipleComponent →
// OW-UNITY-006, type_exists requirement) and unity.component.remove
// (destructive, Transform not removable, RequireComponent-blocked removal
// → OW-UNITY-007).
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.Testing;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>Per-Operation behaviour of the §I.1 Component Operations.</summary>
    internal sealed class ComponentOperationTests
    {
        [SetUp]
        [TearDown]
        public void CleanScene()
        {
            // Every test starts and ends on one clean untitled scene (the
            // OperationTestHarnessTests convention): a scene left dirty or
            // closed by an earlier test cannot cascade into the next one —
            // the harness refuses to run beside unsaved changes, because
            // those changes are the caller's to keep or discard.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private static readonly IReadOnlyDictionary<string, string> AddIn =
            new Dictionary<string, string>
            {
                ["target"] = "unity.GameObjectRef",
                ["componentType"] = "unity.TypeName",
            };

        private static readonly IReadOnlyDictionary<string, string> AddOut =
            new Dictionary<string, string>
            {
                ["component"] = "unity.ComponentRef<unity.Component>",
            };

        private static readonly IReadOnlyDictionary<string, string> RemoveIn =
            new Dictionary<string, string>
            {
                ["component"] = "unity.ComponentRef<unity.Component>",
            };

        private static readonly IReadOnlyDictionary<string, string> NoPorts =
            new Dictionary<string, string>();

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string> inputTypes,
            IReadOnlyDictionary<string, string> outputTypes,
            IChangeRecorder? changes = null) =>
            new UnityStepContext(
                inputs,
                inputTypes,
                outputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                changes ?? new StepChangeRecorder(0, "component"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

        private static JsonValue GoRef(GameObject go) =>
            new RefResolver().ToRef(go, "unity.GameObjectRef");

        private static JsonValue CompRef(Component component) =>
            new RefResolver().ToRef(component, "unity.ComponentRef<unity.Component>");

        private static Dictionary<string, InputValue> AddInputs(
            GameObject target, string componentType) =>
            new Dictionary<string, InputValue>
            {
                ["target"] = InputValue.Present(GoRef(target)),
                ["componentType"] = InputValue.Present(Json("\"" + componentType + "\"")),
            };

        private static string Describe(StepResult result) =>
            result.Kind +
            (result.Error is null
                ? string.Empty
                : " " + result.Error.Code + " " + result.Error.Message);

        private static void AssertFailed(StepResult result, string code, string? port)
        {
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed), () => Describe(result));
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo(code), () => Describe(result));
            if (port is not null)
            {
                Assert.That(result.Error!.Port, Is.EqualTo(port));
            }
        }

        // ── unity.component.add ─────────────────────────────────────────

        [Test]
        public void Add_AddsAnimatorAndOutputsItsComponentRef()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            var changes = new StepChangeRecorder(0, "add");
            var context = Context(
                AddInputs(target, "UnityEngine.Animator"), AddIn, AddOut, changes);

            var result = new ComponentAddOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var animator = target.GetComponent<Animator>();
            Assert.That(animator, Is.Not.Null);
            var output = (JObject)context.Outputs["component"].ToToken();
            Assert.That((string?)output["kind"], Is.EqualTo("component"));
            Assert.That((string?)output["type"], Is.EqualTo("unity.Animator"));
            Assert.That((int?)output["index"], Is.EqualTo(0),
                "index selects within the ref's 'type' component array " +
                "(SPEC §G2 GetComponents(type)[index]): the first Animator is 0");
            var resolved = new RefResolver().Resolve<Component>(context.Outputs["component"]);
            Assert.That(resolved.Ok, Is.True);
            Assert.That(resolved.Value, Is.SameAs(animator));
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Created));
            Assert.That(changes.Records[0].Undoable, Is.True);
        }

        [Test]
        public void Add_OneUndoRemovesTheAddedComponent()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            var undoGroup = Undo.GetCurrentGroup();
            var result = harness.Run(
                ComponentAddOperation.OperationId,
                new { target, componentType = "UnityEngine.Animator" });
            try
            {
                Assert.That(result.Ok, Is.True, () => result.ToString());
                Assert.That(target.GetComponent<Animator>(), Is.Not.Null);

                Undo.PerformUndo();

                Assert.That(target.GetComponent<Animator>(), Is.Null,
                    "one Undo must remove the added component");
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        [Test]
        public void Add_DisallowMultipleComponent_FailsOwUnity006()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            target.AddComponent<Camera>();
            var context = Context(
                AddInputs(target, "UnityEngine.Camera"), AddIn, AddOut);

            var result = new ComponentAddOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_006, "componentType");
            Assert.That(target.GetComponents<Camera>(), Has.Length.EqualTo(1),
                "a refused add leaves the existing component alone");
        }

        [Test]
        public void Add_Preflight_DisallowMultipleComponent_ReportsOwUnity006()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            target.AddComponent<Camera>();

            var preflight = new ComponentAddOperation().Preflight(Context(
                AddInputs(target, "UnityEngine.Camera"), AddIn, AddOut));

            Assert.That(preflight.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(preflight.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_UNITY_006));
        }

        [Test]
        public void Add_UnknownType_FailsOwUnity006()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            var context = Context(
                AddInputs(target, "No.Such.Component"), AddIn, AddOut);

            var result = new ComponentAddOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_006, "componentType");
            Assert.That(target.GetComponents<Component>(), Has.Length.EqualTo(1),
                "only the Transform may be present");
        }

        [Test]
        public void Add_NonComponentType_FailsOwUnity006()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            var context = Context(
                AddInputs(target, "UnityEngine.GameObject"), AddIn, AddOut);

            var result = new ComponentAddOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_006, "componentType");
            Assert.That(target.GetComponents<Component>(), Has.Length.EqualTo(1));
        }

        [Test]
        public void Add_AbstractComponentType_FailsOwUnity006()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            // UnityEngine.Renderer is abstract: it is a Component that can
            // never be added.
            var context = Context(
                AddInputs(target, "UnityEngine.Renderer"), AddIn, AddOut);

            var result = new ComponentAddOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_006, "componentType");
        }

        // ── unity.component.remove ──────────────────────────────────────

        [Test]
        public void Remove_RemovesTheComponent()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            var collider = target.AddComponent<BoxCollider>();
            var changes = new StepChangeRecorder(0, "remove");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["component"] = InputValue.Present(CompRef(collider)),
                },
                RemoveIn, NoPorts, changes);

            var result = new ComponentRemoveOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(target.GetComponent<BoxCollider>(), Is.Null);
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Deleted));
            Assert.That(changes.Records[0].Undoable, Is.True);
        }

        [Test]
        public void Remove_OneUndoRestoresTheRemovedComponent()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            var collider = target.AddComponent<BoxCollider>();
            var undoGroup = Undo.GetCurrentGroup();
            var result = harness.Run(
                ComponentRemoveOperation.OperationId, new { component = collider });
            try
            {
                Assert.That(result.Ok, Is.True, () => result.ToString());
                Assert.That(target.GetComponent<BoxCollider>(), Is.Null);

                Undo.PerformUndo();

                Assert.That(target.GetComponent<BoxCollider>(), Is.Not.Null,
                    "one Undo must restore the removed component");
            }
            finally
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
        }

        [Test]
        public void Remove_Transform_FailsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["component"] = InputValue.Present(CompRef(target.transform)),
                },
                RemoveIn, NoPorts);

            var result = new ComponentRemoveOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_007, "component");
            Assert.That(target.transform, Is.Not.Null,
                "a refused removal leaves the Transform in place");
        }

        [Test]
        public void Remove_RequireComponentBlocked_FailsOwUnity007()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            // HingeJoint declares [RequireComponent(typeof(Rigidbody))]:
            // the Rigidbody it forces onto the GameObject can never be
            // removed while the joint is present.
            target.AddComponent<HingeJoint>();
            var rigidbody = target.GetComponent<Rigidbody>();
            Assert.That(rigidbody, Is.Not.Null,
                "the RequireComponent attribute must add the Rigidbody");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["component"] = InputValue.Present(CompRef(rigidbody)),
                },
                RemoveIn, NoPorts);

            var result = new ComponentRemoveOperation().Execute(context);

            AssertFailed(result, ErrorCodes.OW_UNITY_007, "component");
            Assert.That(target.GetComponent<Rigidbody>(), Is.Not.Null,
                "a RequireComponent-blocked removal leaves the component in place");
        }

        [Test]
        public void Remove_AfterRemoval_VerifiesTheComponentIsGone()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "Rig");
            target.AddComponent<HingeJoint>();
            var rigidbody = target.GetComponent<Rigidbody>()!;
            // §G5 item 6: even attempting the removal directly confirms the
            // component stays — the failed Execute already proved the same
            // verdict through the RequireComponent pre-check, and the
            // component is still on the GameObject.
            var preflight = new ComponentRemoveOperation().Preflight(Context(
                new Dictionary<string, InputValue>
                {
                    ["component"] = InputValue.Present(CompRef(rigidbody)),
                },
                RemoveIn, NoPorts));

            Assert.That(preflight.Diagnostics, Is.Empty);
            Assert.That(target.GetComponent<Rigidbody>(), Is.Not.Null);
        }

        // ── OutputRefinement (SPEC §C1) ─────────────────────────────────

        [Test]
        public void Add_Descriptor_DeclaresComponentRefRefinement()
        {
            var refinement = new ComponentAddOperation().Descriptor.OutputRefinement;

            Assert.That(refinement, Is.Not.Null);
            Assert.That(refinement!.FromParameter, Is.EqualTo("componentType"));
            Assert.That(refinement.OutputPort, Is.EqualTo("component"));
            Assert.That(refinement.Template, Is.EqualTo("unity.ComponentRef<{0}>"));
        }
    }
}
