#nullable enable

// T-W2.5-03 [Edit] tests: local:T-W2.5-03:component-reconcile — the six
// SPEC §F5 Operation-level cases for the two Reconcilable Component
// Operations (add, remove): not executed; executed but StepSucceeded not
// recorded; partial side effect; pre-existing same-name artifact; artifact
// modified by another operation; simulated Editor restart (different
// editorSessionId / discarded handles). One [UnityTest] crosses a real
// domain reload (SPEC §T.3 marker script + RecompileScripts + domain
// sentinel): the Preflight snapshot persisted as JSON still proves
// Completed afterwards.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.Testing;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>§F5 reconciliation of unity.component.add and unity.component.remove.</summary>
    internal sealed class ComponentReconcileTests
    {
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

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveComponentProbe");

        private static string ProbeFile => Path.Combine(ProbeDir, "state.txt");
        private static string MarkerFolder => "Assets/Editor/OpWeaveComponentProbe";
        private const string HarnessRoot = "Assets/OpWeaveTesting";
        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveComponentProbe\n{\n" +
            "    internal static class ReloadMarker { public const int Value = 1; }\n" +
            "}\n";

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // T-W2.4-09: a per-Editor-session token phase-1 stores in SessionState
        // and writes as the probe file's last line; phase-2 reads both to prove
        // the file belongs to this session's phase-1 rather than residue of an
        // earlier run (the ChangeSetDomainReloadTests pattern).
        private const string ProbeTokenKey = "OpWeave.W2409.Component.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume is
        // detected. leg is a method local, so a real reload resets it; the
        // this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[W2409-comp] " + message);

        private static OperationTestHarness? s_reloadHarness;

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string> inputTypes,
            IReadOnlyDictionary<string, string> outputTypes) =>
            new UnityStepContext(
                inputs,
                inputTypes,
                outputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "component"),
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

        private static JsonValue Restarted(JsonValue snapshot)
        {
            // The simulated Editor restart: a different session id with the
            // recorded session handles discarded (SPEC §F5 common rule 3).
            var restarted = (JObject)snapshot.ToToken().DeepClone();
            restarted["editorSessionId"] = "restarted-session";
            return JsonValue.From(restarted);
        }

        // The managed probe component lives in the OpWeaveFixtures.Runtime
        // fixture assembly on purpose: Unity refuses AddComponent for a
        // type defined in an Editor assembly ("it is an editor script"),
        // which is exactly what FingerprintFixtureBehaviour's comment
        // documents — and why the tests reach it by name and drive its
        // static counters through reflection instead of an asmdef
        // reference.
        private static Type ProbeComponentType()
        {
            var type = Type.GetType(
                "OpWeave.Fixtures.ComponentCallbackProbe, OpWeaveFixtures.Runtime");
            Assert.That(type, Is.Not.Null,
                "the OpWeaveFixtures.Runtime probe assembly must be compiled");
            return type!;
        }

        private static void ResetProbeCalls()
        {
            foreach (var name in new[] { "ResetCalls", "AwakeCalls", "EnableCalls" })
            {
                ProbeComponentType().GetField(name)!.SetValue(null, 0);
            }
        }

        private static int ProbeCalls(string name) =>
            (int)ProbeComponentType().GetField(name)!.GetValue(null)!;

        private static string VerdictText(ReconcileResult verdict) =>
            verdict is ReconcileResult.Unknown unknown
                ? "Unknown(" + unknown.Reason + ")"
                : verdict.GetType().Name;

        // ── unity.component.add — the six §F5 cases ──────────────────────

        [Test]
        public void AddReconcile_NotExecuted_ReturnsNotApplied()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAdd");
            var inputs = AddInputs(target, "UnityEngine.Animator");
            var snapshot = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut)).Snapshot;

            var verdict = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        [Test]
        public void AddReconcile_ExecutedUnrecorded_ReturnsCompletedWithOutput()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAddDone");
            var inputs = AddInputs(target, "UnityEngine.Animator");
            var snapshot = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut)).Snapshot;
            var ran = new ComponentAddOperation().Execute(Context(inputs, AddIn, AddOut));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                () => VerdictText(verdict));
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.That(outputs.ContainsKey("component"), Is.True);
            var resolved = new RefResolver().Resolve<Component>(outputs["component"]);
            Assert.That(resolved.Ok, Is.True);
            Assert.That(resolved.Value, Is.TypeOf<Animator>());
        }

        [Test]
        public void AddReconcile_PartialSideEffect_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAddPartial");
            var inputs = AddInputs(target, "UnityEngine.Animator");
            var snapshot = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut)).Snapshot;
            // Two new components where the step could only have added one.
            target.AddComponent<Animator>();
            target.AddComponent<BoxCollider>();

            var verdict = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => VerdictText(verdict));
        }

        [Test]
        public void AddReconcile_PreexistingSameTypeComponent_ReturnsNotApplied()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAddSame");
            // The managed probe is inside the snapshot's pre-state, so it is
            // not the step's artifact (§F5 common rule 4) — and the add
            // stays legal because a managed MonoBehaviour is addable more
            // than once. (Unity 6000 refuses a second Animator even though
            // it carries no managed DisallowMultipleComponent attribute —
            // the engine's native single-instance flag — so a same-type
            // add of it is OW-UNITY-006, never a real snapshot.)
            var probeType = ProbeComponentType();
            target.AddComponent(probeType);
            var inputs = AddInputs(target, probeType.FullName!);
            var snapshot = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut)).Snapshot;

            var verdict = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>(),
                "the pre-existing component must not prove Completed");
        }

        [Test]
        public void AddReconcile_ModifiedArtifact_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAddChanged");
            var inputs = AddInputs(target, "UnityEngine.Animator");
            var snapshot = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut)).Snapshot;
            var ran = new ComponentAddOperation().Execute(Context(inputs, AddIn, AddOut));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));
            // Another operation touched the artifact's serialized content.
            target.GetComponent<Animator>().applyRootMotion = true;

            var verdict = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "a modified artifact no longer matches the recorded defaults");
        }

        [Test]
        public void AddReconcile_ExistingComponentModified_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAddSibling");
            var sibling = target.AddComponent<BoxCollider>();
            var inputs = AddInputs(target, "UnityEngine.Animator");
            // The verdict must be reached through a full Preflight
            // snapshot: every member Reconcile reads, and a recorded
            // content for the sibling — an empty or content-less snapshot
            // can never exercise the normalized-content comparison this
            // case exists to prove.
            var preflight = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut));
            Assert.That(preflight.Diagnostics, Is.Empty,
                "a legal add on the sibling target must write a full snapshot");
            var snapshotToken = (JObject)preflight.Snapshot.ToToken();
            Assert.That(snapshotToken["target"], Is.InstanceOf<JObject>());
            Assert.That(snapshotToken["componentType"], Is.InstanceOf<JValue>());
            Assert.That(snapshotToken["expectedAdded"], Is.InstanceOf<JArray>());
            Assert.That(snapshotToken["editorSessionId"], Is.InstanceOf<JValue>());
            var recorded = (JArray)snapshotToken["components"]!;
            Assert.That(recorded, Has.Count.EqualTo(2),
                "the snapshot must record the Transform and the sibling");
            foreach (var entry in recorded)
            {
                Assert.That(entry is JObject obj && obj["content"] is JObject, Is.True,
                    "every recorded component must carry the content the comparison reads");
            }
            var snapshot = preflight.Snapshot;
            var ran = new ComponentAddOperation().Execute(Context(inputs, AddIn, AddOut));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));
            // A pre-existing component's serialized data changed after
            // Preflight — its type · order · handle are intact, so only
            // the recorded content disproves §F5's "他の Component が
            // 変わらず". Reconcile must not claim Completed on handle
            // identity alone.
            sibling.size = new Vector3(2f, 3f, 4f);

            var modified = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), snapshot);
            Assert.That(modified, Is.TypeOf<ReconcileResult.Unknown>(),
                () => VerdictText(modified));
            Assert.That(((ReconcileResult.Unknown)modified).Reason,
                Is.EqualTo(
                    "The target's pre-existing components changed since the snapshot."),
                "the verdict must come from the recorded-content comparison, " +
                "not a missing snapshot member");

            // Restoring the recorded values restores the Completed proof:
            // the verdict tracks serialized content, not the edit itself.
            sibling.size = Vector3.one;
            var restored = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), snapshot);
            Assert.That(restored, Is.TypeOf<ReconcileResult.Completed>(),
                () => VerdictText(restored));
        }

        // F3: a user-defined serialized field named like a Unity hierarchy
        // link (m_Father) must stay in the compared content — only the
        // Unity-owned link properties are excluded, never by name alone.
        [Test]
        public void AddReconcile_UserFieldNamedLikeLinkage_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAddNamed");
            var sibling = target.AddComponent(ProbeComponentType());
            Assert.That(sibling, Is.Not.Null);
            var inputs = AddInputs(target, "UnityEngine.Animator");
            var snapshot = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut)).Snapshot;
            var ran = new ComponentAddOperation().Execute(Context(inputs, AddIn, AddOut));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));
            // m_Father is a plain user field on the managed sibling; only a
            // Transform's own m_Father link is excluded from comparison.
            var serialized = new SerializedObject(sibling);
            serialized.FindProperty("m_Father")!.intValue = 42;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var verdict = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => VerdictText(verdict));
        }

        [Test]
        public void AddPreflight_ManagedComponentType_RunsNoCallbacks()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcProbe");
            ResetProbeCalls();
            var inputs = AddInputs(target, ProbeComponentType().FullName!);

            var preflight = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut));

            Assert.That(preflight.Diagnostics, Is.Empty);
            Assert.That(ProbeCalls("ResetCalls"), Is.EqualTo(0),
                "Preflight must not instantiate the requested component (SPEC §C2)");
            Assert.That(ProbeCalls("AwakeCalls"), Is.EqualTo(0));
            Assert.That(ProbeCalls("EnableCalls"), Is.EqualTo(0));
            Assert.That(target.GetComponent(ProbeComponentType()), Is.Null);
            Assert.That(GameObject.Find("OpWeaveComponentProbe"), Is.Null,
                "Preflight leaves no probe object in a user scene");
        }

        [Test]
        public void AddReconcile_ManagedComponentType_UnverifiableDefaults_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcManaged");
            var inputs = AddInputs(target, ProbeComponentType().FullName!);
            var snapshot = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut)).Snapshot;
            var ran = new ComponentAddOperation().Execute(Context(inputs, AddIn, AddOut));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), snapshot);

            // A managed component's just-added defaults cannot be captured
            // without running its callbacks, so the content check fails
            // closed (§F5 common rule 6) — never a guessed Completed.
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => VerdictText(verdict));
        }

        [Test]
        public void AddReconcile_SimulatedRestart_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAddRestart");
            var inputs = AddInputs(target, "UnityEngine.Animator");
            var snapshot = new ComponentAddOperation()
                .Preflight(Context(inputs, AddIn, AddOut)).Snapshot;
            var ran = new ComponentAddOperation().Execute(Context(inputs, AddIn, AddOut));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new ComponentAddOperation().Reconcile(
                Context(inputs, AddIn, AddOut), Restarted(snapshot));

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "a different session id can prove nothing (§F5 rule 3)");
        }

        // Correction-4 regression: Unity 6000 refuses a second add of a
        // single-instance engine type on the detached probe even when the
        // target holds none — that refusal must not by itself produce
        // OW-UNITY-006 on a target the engine would accept the add for.
        [Test]
        public void AddPreflight_SingleInstanceTypeOnFreshTarget_WritesSnapshot()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAddFresh");

            var preflight = new ComponentAddOperation().Preflight(Context(
                AddInputs(target, "UnityEngine.Animator"), AddIn, AddOut));

            Assert.That(preflight.Diagnostics, Is.Empty,
                "a legal add on a fresh target must produce no diagnostics");
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That(snapshot["expectedAdded"], Is.InstanceOf<JArray>(),
                "the snapshot must carry the additions Reconcile needs");
        }

        // Correction-4 regression: when the requested defaults cannot be
        // captured (a managed type's callbacks must not run in Preflight),
        // the snapshot must still carry every member Reconcile reads —
        // missing members are exactly the fail-closed Unknown the
        // correction-3 regression produced.
        [Test]
        public void AddPreflight_NullDefaultsSnapshot_CarriesEveryMember()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcAddManaged");

            var preflight = new ComponentAddOperation().Preflight(Context(
                AddInputs(target, ProbeComponentType().FullName!), AddIn, AddOut));

            Assert.That(preflight.Diagnostics, Is.Empty);
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That(snapshot["target"], Is.InstanceOf<JObject>());
            Assert.That(snapshot["componentType"], Is.InstanceOf<JValue>());
            Assert.That(snapshot["components"], Is.InstanceOf<JArray>());
            Assert.That(snapshot["expectedAdded"], Is.InstanceOf<JArray>());
            Assert.That(snapshot["editorSessionId"], Is.InstanceOf<JValue>());
            var expected = (JArray)snapshot["expectedAdded"]!;
            Assert.That(expected, Has.Count.EqualTo(1));
            Assert.That(((JObject)expected[0]!)["type"], Is.Not.Null,
                "a null-defaults entry still records the planned type");
        }

        // Regression (T-W4.3-03 correction): the defaults probe lives in a
        // detached preview scene, so an untitled scene — the state this
        // fixture's cleanup always leaves loaded — must not cost the
        // snapshot its expected content. Before the fix the probe's
        // additive NewScene was refused here, expectedAdded carried no
        // content and an adopted component.add could only answer Unknown
        // (NeedsReview, OW-RUN-011) even with perfect evidence.
        [Test]
        public void AddReconcile_UntitledSceneLoaded_ReturnsCompleted()
        {
            Assert.That(SceneOps.UntitledSceneLoaded(), Is.True,
                "fixture: the cleanup must leave an untitled scene loaded");
            var target = new GameObject("RcAddUntitled");
            try
            {
                var inputs = AddInputs(target, "UnityEngine.BoxCollider");
                var preflight = new ComponentAddOperation()
                    .Preflight(Context(inputs, AddIn, AddOut));
                Assert.That(preflight.Diagnostics, Is.Empty,
                    "a legal add on a fresh target must produce no diagnostics");
                var snapshot = (JObject)preflight.Snapshot.ToToken();
                var expected = (JArray)snapshot["expectedAdded"]!;
                Assert.That(expected, Has.Count.EqualTo(1));
                Assert.That(
                    ((JObject)expected[0]!)["content"], Is.InstanceOf<JObject>(),
                    "the detached probe captures the additions' defaults " +
                    "beside an untitled scene");
                var ran = new ComponentAddOperation()
                    .Execute(Context(inputs, AddIn, AddOut));
                Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

                var verdict = new ComponentAddOperation().Reconcile(
                    Context(inputs, AddIn, AddOut), preflight.Snapshot);

                Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                    () => VerdictText(verdict));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        // ── unity.component.remove — the six §F5 cases ──────────────────

        [Test]
        public void RemoveReconcile_NotExecuted_ReturnsNotApplied()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcDel");
            var collider = target.AddComponent<BoxCollider>();
            var inputs = new Dictionary<string, InputValue>
            {
                ["component"] = InputValue.Present(CompRef(collider)),
            };
            var snapshot = new ComponentRemoveOperation()
                .Preflight(Context(inputs, RemoveIn, NoPorts)).Snapshot;

            var verdict = new ComponentRemoveOperation().Reconcile(
                Context(inputs, RemoveIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
            Assert.That(collider, Is.Not.Null);
        }

        [Test]
        public void RemoveReconcile_ExecutedUnrecorded_ReturnsCompleted()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcDelDone");
            var collider = target.AddComponent<BoxCollider>();
            target.AddComponent<SphereCollider>();
            var inputs = new Dictionary<string, InputValue>
            {
                ["component"] = InputValue.Present(CompRef(collider)),
            };
            var snapshot = new ComponentRemoveOperation()
                .Preflight(Context(inputs, RemoveIn, NoPorts)).Snapshot;
            var ran = new ComponentRemoveOperation().Execute(Context(inputs, RemoveIn, NoPorts));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new ComponentRemoveOperation().Reconcile(
                Context(inputs, RemoveIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                () => VerdictText(verdict));
            Assert.That(target.GetComponent<SphereCollider>(), Is.Not.Null,
                "only the target may be gone");
        }

        [Test]
        public void RemoveReconcile_PartialSideEffect_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcDelPartial");
            var collider = target.AddComponent<BoxCollider>();
            var sibling = target.AddComponent<SphereCollider>();
            var inputs = new Dictionary<string, InputValue>
            {
                ["component"] = InputValue.Present(CompRef(collider)),
            };
            var snapshot = new ComponentRemoveOperation()
                .Preflight(Context(inputs, RemoveIn, NoPorts)).Snapshot;
            // More than the target is gone: the sibling vanished too.
            UnityEngine.Object.DestroyImmediate(collider);
            UnityEngine.Object.DestroyImmediate(sibling);

            var verdict = new ComponentRemoveOperation().Reconcile(
                Context(inputs, RemoveIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "another missing component must never be Completed");
        }

        [Test]
        public void RemoveReconcile_SubstituteComponent_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcDelSame");
            var collider = target.AddComponent<BoxCollider>();
            var inputs = new Dictionary<string, InputValue>
            {
                ["component"] = InputValue.Present(CompRef(collider)),
            };
            var snapshot = new ComponentRemoveOperation()
                .Preflight(Context(inputs, RemoveIn, NoPorts)).Snapshot;
            UnityEngine.Object.DestroyImmediate(collider);
            // A foreign same-type component takes the target's place: it is
            // a different identity, never the deleted artifact's survival.
            target.AddComponent<BoxCollider>();

            var verdict = new ComponentRemoveOperation().Reconcile(
                Context(inputs, RemoveIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>());
        }

        [Test]
        public void RemoveReconcile_SiblingComponentModified_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcDelSibling");
            var collider = target.AddComponent<BoxCollider>();
            var sibling = target.AddComponent<SphereCollider>();
            var inputs = new Dictionary<string, InputValue>
            {
                ["component"] = InputValue.Present(CompRef(collider)),
            };
            var snapshot = new ComponentRemoveOperation()
                .Preflight(Context(inputs, RemoveIn, NoPorts)).Snapshot;
            var ran = new ComponentRemoveOperation().Execute(Context(inputs, RemoveIn, NoPorts));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));
            // Same component list minus the target, but the surviving
            // sibling's serialized data changed — "list minus target
            // unchanged" is disproven only through recorded content.
            sibling.radius = 0.75f;

            var verdict = new ComponentRemoveOperation().Reconcile(
                Context(inputs, RemoveIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => VerdictText(verdict));
        }

        [Test]
        public void RemoveReconcile_ComponentAddedElsewhere_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcDelMoved");
            var collider = target.AddComponent<BoxCollider>();
            var bystander = target.AddComponent<SphereCollider>();
            var inputs = new Dictionary<string, InputValue>
            {
                ["component"] = InputValue.Present(CompRef(collider)),
            };
            var snapshot = new ComponentRemoveOperation()
                .Preflight(Context(inputs, RemoveIn, NoPorts)).Snapshot;
            // The artifact was not deleted; a different component vanished.
            UnityEngine.Object.DestroyImmediate(bystander);

            var verdict = new ComponentRemoveOperation().Reconcile(
                Context(inputs, RemoveIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "a non-target component delta must never read as the step's deletion");
        }

        [Test]
        public void RemoveReconcile_SimulatedRestart_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = HarnessObjects.NewGameObject(harness, "RcDelRestart");
            var collider = target.AddComponent<BoxCollider>();
            var inputs = new Dictionary<string, InputValue>
            {
                ["component"] = InputValue.Present(CompRef(collider)),
            };
            var snapshot = new ComponentRemoveOperation()
                .Preflight(Context(inputs, RemoveIn, NoPorts)).Snapshot;
            var ran = new ComponentRemoveOperation().Execute(Context(inputs, RemoveIn, NoPorts));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new ComponentRemoveOperation().Reconcile(
                Context(inputs, RemoveIn, NoPorts), Restarted(snapshot));

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>());
        }

        // ── domain reload (SPEC §T.3) ───────────────────────────────────

        private static bool CleanupReloadArtifacts()
        {
            var dirty = false;
            if (s_reloadHarness is { } abandoned)
            {
                s_reloadHarness = null;
                try { abandoned.Dispose(); }
                catch (Exception) { /* cleanup is best-effort */ }
            }
            s_domainSentinel = null;
            // One clean untitled scene always ends the cleanup (the
            // OperationTestHarnessTests convention): a stray harness scene
            // or a caller scene left dirty by an earlier test cannot
            // cascade into the next one — the harness refuses to run
            // beside unsaved changes.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(HarnessRoot))
            {
                AssetDatabase.DeleteAsset(HarnessRoot);
            }
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { }
            }
            if (AssetDatabase.IsValidFolder(MarkerFolder))
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
                dirty = true;
            }
            if (Directory.Exists(MarkerDisk))
            {
                Directory.Delete(MarkerDisk, recursive: true);
                dirty = true;
            }
            return dirty;
        }

        // Setup and teardown must never yield an ExpectDomainReload
        // instruction (RecompileScripts): UTF's PrepareForDomainReload records
        // the CURRENT TestEnumerator's program counter — still a different
        // test's method enumerator during setup and teardown — and the resume
        // then applies that stale PC to the next test method's fresh
        // enumerator, jumping it past phase-1 entirely when the captured
        // enumerator was abandoned mid-state. The marker deletion queues its
        // compile under the runner's LockReloadAssemblies, so the reload it
        // needs drains at the next test body's own
        // `yield return new RecompileScripts()`, whose captured PC is the
        // correct one.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup cleanup enter; probeDir=" + Directory.Exists(ProbeDir));
            CleanupReloadArtifacts();
            yield break;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            Crumb("teardown cleanup enter; probeDir=" + Directory.Exists(ProbeDir));
            CleanupReloadArtifacts();
            yield break;
        }

        // Phase 1 (pre-reload): run both Reconcilable Operations on a
        // harness GameObject and persist each Preflight snapshot as JSON —
        // the same evidence a StepStarted journal record would hold.
        private static string RunPhaseBeforeReload()
        {
            try
            {
                var harness = s_reloadHarness = OperationTestHarness.Create();
                var target = HarnessObjects.NewGameObject(harness, "ReloadComponentRig");

                var addInputs = AddInputs(target, "UnityEngine.Animator");
                var addSnapshot = new ComponentAddOperation()
                    .Preflight(Context(addInputs, AddIn, AddOut)).Snapshot;
                var ran = new ComponentAddOperation()
                    .Execute(Context(addInputs, AddIn, AddOut));
                if (ran.Kind != StepResultKind.Succeeded)
                {
                    return "add execute failed: " + (ran.Error?.Message ?? "?");
                }

                var doomed = target.AddComponent<BoxCollider>();
                var removeInputs = new Dictionary<string, InputValue>
                {
                    ["component"] = InputValue.Present(CompRef(doomed)),
                };
                var removeSnapshot = new ComponentRemoveOperation()
                    .Preflight(Context(removeInputs, RemoveIn, NoPorts)).Snapshot;
                var deleted = new ComponentRemoveOperation()
                    .Execute(Context(removeInputs, RemoveIn, NoPorts));
                if (deleted.Kind != StepResultKind.Succeeded)
                {
                    return "remove execute failed: " + (deleted.Error?.Message ?? "?");
                }

                Directory.CreateDirectory(ProbeDir);
                var token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
                File.WriteAllText(
                    ProbeFile,
                    addSnapshot.CanonicalText + "\n" +
                    removeSnapshot.CanonicalText + "\n" +
                    token + "\n");
                Crumb("probe written: " + ProbeFile);
                Directory.CreateDirectory(MarkerDisk);
                File.WriteAllText(
                    Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
                AssetDatabase.Refresh();
                ArmDomainSentinel();
                return string.Empty;
            }
            catch (Exception e)
            {
                return "phase-1 threw " + e.GetType().Name + ": " + e.Message;
            }
        }

        // Phase 2 (post-reload): rebuild the step contexts and reconcile
        // the persisted snapshots — same session, handles alive, so both
        // verdicts must be Completed with the uninterrupted-run outputs.
        private static string RunPhaseAfterReload()
        {
            try
            {
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(ProbeFile))
                {
                    return expectedToken.Length == 0
                        ? "phase-1 never executed in this Editor session (" +
                            ProbeFile + " was never written; probeDir exists=" +
                            Directory.Exists(ProbeDir) + ")"
                        : "the persisted probe state " + ProbeFile +
                            " was deleted after phase-1 wrote it";
                }
                var lines = File.ReadAllLines(ProbeFile);
                if (expectedToken.Length == 0 || lines.Length < 3 ||
                    lines[2] != expectedToken)
                {
                    return "the persisted probe state is stale — a previous " +
                        "Editor session wrote " + ProbeFile;
                }
                var rig = GameObject.Find("ReloadComponentRig");
                if (rig is null)
                {
                    return "the probe GameObject is missing after the reload";
                }
                var addInputs = AddInputs(rig, "UnityEngine.Animator");
                var addVerdict = new ComponentAddOperation().Reconcile(
                    Context(addInputs, AddIn, AddOut), JsonValue.Parse(lines[0]).Value!);
                if (addVerdict is not ReconcileResult.Completed completed)
                {
                    return "add reconcile was not Completed: " + VerdictText(addVerdict);
                }
                var resolved = new RefResolver().Resolve<Component>(
                    completed.Outputs["component"]);
                if (!resolved.Ok || resolved.Value is not Animator)
                {
                    return "the add output does not name the added Animator";
                }
                var removeVerdict = new ComponentRemoveOperation().Reconcile(
                    Context(
                        new Dictionary<string, InputValue>(), RemoveIn, NoPorts),
                    JsonValue.Parse(lines[1]).Value!);
                if (removeVerdict is not ReconcileResult.Completed)
                {
                    return "remove reconcile was not Completed: " + VerdictText(removeVerdict);
                }
                if (rig.GetComponent<BoxCollider>() != null)
                {
                    return "the removed BoxCollider is still on the GameObject";
                }
                return string.Empty;
            }
            catch (Exception e)
            {
                return "phase-2 threw " + e.GetType().Name + ": " + e.Message;
            }
        }

        // The phase-1 probe proves phase-1 ran in THIS Editor session: the file
        // must exist AND its last line must equal the token phase-1 stored in
        // SessionState. Both Library/ and SessionState survive the reload, so a
        // stale-resume hijack (phase-1 skipped: no file, no token) and residue
        // of an earlier session (file yes, token no) both fail the check — the
        // caller then re-drives a real phase-1 instead of skipping into
        // phase-2.
        private static bool ProbeWrittenThisSession()
        {
            if (!File.Exists(ProbeFile))
            {
                return false;
            }
            var expectedToken = SessionState.GetString(ProbeTokenKey, "");
            if (expectedToken.Length == 0)
            {
                return false;
            }
            var lines = File.ReadAllLines(ProbeFile);
            return lines.Length > 0 && lines[lines.Length - 1] == expectedToken;
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Reconcile_SurvivesADomainReload()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 returned: " + phase1);
            Assert.That(phase1, Is.Empty, "pre-reload phase failed: " + phase1);

            // T-W2.4-09 stale-resume self-heal: a predecessor's captured UTF
            // resume PC applies to this fresh enumerator and lands it at one of
            // the two post-yield states inside this loop — with every local
            // (leg included) reset to default. Both continuations drain, then
            // MUST pass the this-session probe check: a probe phase-1 never
            // wrote re-drives a real phase-1 + compile + reload instead of
            // skipping silently into phase-2. If the probe still cannot be
            // produced after the bound, the loop exits and phase-2's existing
            // never-executed diagnostic fails loudly.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (DomainSentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(DomainSentinelAlive,
                    "the probe compile did not produce a domain reload");

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // A marker file an abandoned leg left behind carries identical
                // bytes, so deleting the probe assets guarantees the re-write
                // queues a real compile rather than a no-op Refresh.
                CleanupReloadArtifacts();
                var redrive = RunPhaseBeforeReload();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " -> " + redrive);
                Assert.That(redrive, Is.Empty,
                    "re-driven pre-reload phase failed: " + redrive);
            }

            var phase2 = RunPhaseAfterReload();
            Crumb("phase-2 returned: " + phase2);
            Assert.That(phase2, Is.Empty, "post-reload phase failed: " + phase2);
        }
    }
}
