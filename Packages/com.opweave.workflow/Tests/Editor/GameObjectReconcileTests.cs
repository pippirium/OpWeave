#nullable enable

// T-W2.5-02 [Edit] tests: local:T-W2.5-02:gameobject-reconcile — the six
// SPEC §F5 Operation-level cases for the two Reconcilable GameObject
// Operations (create, delete): not executed; executed but StepSucceeded
// not recorded; partial side effect; pre-existing same-name artifact;
// artifact modified by another operation; simulated Editor restart
// (different editorSessionId / discarded handles). One [UnityTest] for
// each Operation crosses a real domain reload (SPEC §T.3 marker script +
// RecompileScripts + domain sentinel): the Preflight snapshot persisted as
// JSON still proves Completed afterwards.
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
    /// <summary>§F5 reconciliation of unity.gameobject.create and unity.gameobject.delete.</summary>
    internal sealed class GameObjectReconcileTests
    {
        private static readonly IReadOnlyDictionary<string, string> CreateIn =
            new Dictionary<string, string>
            {
                ["name"] = "core.String",
                ["scene"] = "unity.SceneRef",
                ["parent"] = "unity.GameObjectRef",
                ["primitive"] = "core.String",
                ["position"] = "unity.Vector3",
            };

        private static readonly IReadOnlyDictionary<string, string> GoIn =
            new Dictionary<string, string> { ["target"] = "unity.GameObjectRef" };

        private static readonly IReadOnlyDictionary<string, string> GoOut =
            new Dictionary<string, string> { ["gameObject"] = "unity.GameObjectRef" };

        private static readonly IReadOnlyDictionary<string, string> NoPorts =
            new Dictionary<string, string>();

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveGameObjectProbe");

        private static string ProbeFile => Path.Combine(ProbeDir, "state.txt");
        private static string MarkerFolder => "Assets/Editor/OpWeaveGameObjectProbe";
        private const string HarnessRoot = "Assets/OpWeaveTesting";
        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveGameObjectProbe\n{\n" +
            "    internal static class ReloadMarker { public const int Value = 1; }\n" +
            "}\n";

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // T-W2.4-09: a per-Editor-session token phase-1 stores in SessionState
        // and writes as the probe file's last line; phase-2 reads both to prove
        // the file belongs to this session's phase-1 rather than residue of an
        // earlier run (the ChangeSetDomainReloadTests pattern).
        private const string ProbeTokenKey = "OpWeave.W2409.GameObject.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume is
        // detected. leg is a method local, so a real reload resets it; the
        // this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[W2409-go] " + message);

        // The [UnityTest]'s in-memory harness owner. A real domain reload
        // clears the static with every other — the harness's saved scene and
        // folder are then disk residue for CleanupReloadArtifacts. When the
        // test fails before the reload happens, the harness is still live
        // and still registered in the harness registry: the cleanup disposes
        // it so its scene close, object destruction, folder removal and
        // registry removal all run through its own ordered Dispose.
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
                new StepChangeRecorder(0, "go"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

        private static JsonValue GoRef(GameObject go) =>
            new RefResolver().ToRef(go, "unity.GameObjectRef");

        private static JsonValue SceneRef(Scene scene) =>
            JsonValue.From(new JObject
            {
                ["kind"] = "scene",
                ["path"] = scene.path,
                ["guid"] = AssetDatabase.AssetPathToGUID(scene.path),
            });

        private static GameObject Plain(
            OperationTestHarness harness, string name, Transform? parent = null)
        {
            var go = parent is null
                ? HarnessObjects.NewGameObject(harness, name)
                : new GameObject(name);
            if (parent is not null)
            {
                go.transform.SetParent(parent, false);
            }
            return go;
        }

        private static JsonValue Restarted(JsonValue snapshot)
        {
            // The simulated Editor restart: a different session id with the
            // recorded session handles discarded (SPEC §F5 common rule 3).
            var restarted = (JObject)snapshot.ToToken().DeepClone();
            restarted["editorSessionId"] = "restarted-session";
            return JsonValue.From(restarted);
        }

        // Diagnostic for the F4 primitive-evidence check: one line naming
        // every input the Reconcile content check compares (component list,
        // mesh name and asset path, built-in resource identity, collider
        // type and values, transform positions), so a head-bound Unknown
        // shows exactly which comparison rejected a genuine primitive.
        private static void LogEvidence(GameObject? go)
        {
            if (go is null)
            {
                Debug.Log("[W2502-evidence] no created object found");
                return;
            }
            var mesh = go.GetComponent<MeshFilter>() is { } filter
                ? filter.sharedMesh
                : null;
            var builtin = mesh is null
                ? null
                : Resources.GetBuiltinResource<Mesh>(mesh.name + ".fbx");
            var collider = go.GetComponent<Collider>();
            var colliderText = collider switch
            {
                null => "none",
                BoxCollider box =>
                    "BoxCollider(center=" + box.center + " size=" + box.size + ")",
                SphereCollider sphere =>
                    "SphereCollider(center=" + sphere.center +
                    " radius=" + sphere.radius + ")",
                CapsuleCollider capsule =>
                    "CapsuleCollider(center=" + capsule.center +
                    " radius=" + capsule.radius + " height=" + capsule.height +
                    " direction=" + capsule.direction + ")",
                MeshCollider meshCollider =>
                    "MeshCollider(sharedMesh=" +
                    (meshCollider.sharedMesh is null
                        ? "<null>" : meshCollider.sharedMesh.name) + ")",
                _ => collider.GetType().Name,
            };
            Debug.Log(
                "[W2502-evidence] components=" +
                string.Join(",", GameObjectEvidence.ComponentNames(go)) +
                " mesh=" + (mesh is null ? "<null>" : mesh.name) +
                " meshPath=" +
                (mesh is null ? "<null>" : AssetDatabase.GetAssetPath(mesh)) +
                " meshIsBuiltin=" + (mesh is not null && mesh == builtin) +
                " collider=" + colliderText +
                " position=" + go.transform.position +
                " localPosition=" + go.transform.localPosition);
        }

        private static Dictionary<string, InputValue> CreateInputs(
            string name, string? primitive = null, string? position = null)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["name"] = InputValue.Present(Json("\"" + name + "\"")),
            };
            if (primitive is not null)
            {
                inputs["primitive"] = InputValue.Present(Json("\"" + primitive + "\""));
            }
            if (position is not null)
            {
                inputs["position"] = InputValue.Present(Json(position));
            }
            return inputs;
        }

        // ── unity.gameobject.create — the six §F5 cases ─────────────────

        [Test]
        public void CreateReconcile_NotExecuted_ReturnsNotApplied()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var inputs = CreateInputs("RcNew");
            var snapshot = new GameObjectCreateOperation()
                .Preflight(Context(inputs, CreateIn, GoOut)).Snapshot;

            var verdict = new GameObjectCreateOperation().Reconcile(
                Context(inputs, CreateIn, GoOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        [Test]
        public void CreateReconcile_ExecutedUnrecorded_ReturnsCompletedWithOutput()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var inputs = CreateInputs("RcDone", "cube", "{\"x\":1,\"y\":2,\"z\":3}");
            var snapshot = new GameObjectCreateOperation()
                .Preflight(Context(inputs, CreateIn, GoOut)).Snapshot;
            var ran = new GameObjectCreateOperation().Execute(Context(inputs, CreateIn, GoOut));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new GameObjectCreateOperation().Reconcile(
                Context(inputs, CreateIn, GoOut), snapshot);
            LogEvidence(GameObject.Find("RcDone"));

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.That(outputs.ContainsKey("gameObject"), Is.True);
            var resolved = new RefResolver().Resolve<GameObject>(outputs["gameObject"]);
            Assert.That(resolved.Ok, Is.True);
            Assert.That(resolved.Value!.name, Is.EqualTo("RcDone"));
        }

        [Test]
        public void CreateReconcile_PartialSideEffect_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var inputs = CreateInputs("RcPartial");
            var snapshot = new GameObjectCreateOperation()
                .Preflight(Context(inputs, CreateIn, GoOut)).Snapshot;
            // Two new children where the step could only have added one.
            Plain(harness, "RcPartial");
            Plain(harness, "RcOther");

            var verdict = new GameObjectCreateOperation().Reconcile(
                Context(inputs, CreateIn, GoOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>());
        }

        [Test]
        public void CreateReconcile_PreexistingSameNameArtifact_ReturnsNotApplied()
        {
            using var harness = OperationTestHarness.Create();
            var before = Plain(harness, "RcSame");
            var inputs = CreateInputs("RcSame");
            // The same-name object is inside the snapshot's pre-state, so it
            // is not the step's artifact (§F5 common rule 4).
            var snapshot = new GameObjectCreateOperation()
                .Preflight(Context(inputs, CreateIn, GoOut)).Snapshot;

            var verdict = new GameObjectCreateOperation().Reconcile(
                Context(inputs, CreateIn, GoOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>(),
                "the pre-existing same-name object must not prove Completed");
            Assert.That(before, Is.Not.Null);
        }

        [Test]
        public void CreateReconcile_ModifiedArtifact_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var inputs = CreateInputs("RcChanged");
            var snapshot = new GameObjectCreateOperation()
                .Preflight(Context(inputs, CreateIn, GoOut)).Snapshot;
            var ran = new GameObjectCreateOperation().Execute(Context(inputs, CreateIn, GoOut));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));
            var created = GameObject.Find("RcChanged");
            Assert.That(created, Is.Not.Null);
            created!.name = "RcChanged-Renamed";

            var verdict = new GameObjectCreateOperation().Reconcile(
                Context(inputs, CreateIn, GoOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "a renamed artifact no longer matches the recorded inputs");
        }

        [Test]
        public void CreateReconcile_QuadForPlane_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var inputs = CreateInputs("RcPlane");
            inputs["primitive"] = InputValue.Present(Json("\"plane\""));
            var snapshot = new GameObjectCreateOperation()
                .Preflight(Context(inputs, CreateIn, GoOut)).Snapshot;
            // The same component list as a plane but a different primitive:
            // name and position match while the mesh content does not (§F5).
            var substituted = GameObject.CreatePrimitive(PrimitiveType.Quad);
            substituted.name = "RcPlane";

            var verdict = new GameObjectCreateOperation().Reconcile(
                Context(inputs, CreateIn, GoOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "a quad must not prove a plane's Completed");
        }

        [Test]
        public void CreateReconcile_CylinderForCapsule_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var inputs = CreateInputs("RcCapsule");
            inputs["primitive"] = InputValue.Present(Json("\"capsule\""));
            var snapshot = new GameObjectCreateOperation()
                .Preflight(Context(inputs, CreateIn, GoOut)).Snapshot;
            var substituted = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            substituted.name = "RcCapsule";

            var verdict = new GameObjectCreateOperation().Reconcile(
                Context(inputs, CreateIn, GoOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "a cylinder must not prove a capsule's Completed");
        }

        [Test]
        public void CreateReconcile_SimulatedRestart_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            HarnessObjects.NewGameObject(harness, "Marker");
            var inputs = CreateInputs("RcRestart");
            var snapshot = new GameObjectCreateOperation()
                .Preflight(Context(inputs, CreateIn, GoOut)).Snapshot;
            var ran = new GameObjectCreateOperation().Execute(Context(inputs, CreateIn, GoOut));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new GameObjectCreateOperation().Reconcile(
                Context(inputs, CreateIn, GoOut), Restarted(snapshot));

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "a different session id can prove nothing (§F5 rule 3)");
        }

        // ── unity.gameobject.delete — the six §F5 cases ─────────────────

        [Test]
        public void DeleteReconcile_NotExecuted_ReturnsNotApplied()
        {
            using var harness = OperationTestHarness.Create();
            var target = Plain(harness, "DelNew");
            var inputs = new Dictionary<string, InputValue>
            {
                ["target"] = InputValue.Present(GoRef(target)),
            };
            var snapshot = new GameObjectDeleteOperation()
                .Preflight(Context(inputs, GoIn, NoPorts)).Snapshot;

            var verdict = new GameObjectDeleteOperation().Reconcile(
                Context(inputs, GoIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
            Assert.That(target, Is.Not.Null);
        }

        [Test]
        public void DeleteReconcile_ExecutedUnrecorded_ReturnsCompleted()
        {
            using var harness = OperationTestHarness.Create();
            var target = Plain(harness, "DelDone");
            Plain(harness, "DelSibling");
            var inputs = new Dictionary<string, InputValue>
            {
                ["target"] = InputValue.Present(GoRef(target)),
            };
            var snapshot = new GameObjectDeleteOperation()
                .Preflight(Context(inputs, GoIn, NoPorts)).Snapshot;
            var ran = new GameObjectDeleteOperation().Execute(Context(inputs, GoIn, NoPorts));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new GameObjectDeleteOperation().Reconcile(
                Context(inputs, GoIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            Assert.That(GameObject.Find("DelSibling"), Is.Not.Null,
                "only the target may be gone");
        }

        [Test]
        public void DeleteReconcile_PartialSideEffect_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = Plain(harness, "DelPartial");
            var sibling = Plain(harness, "DelSibling2");
            var inputs = new Dictionary<string, InputValue>
            {
                ["target"] = InputValue.Present(GoRef(target)),
            };
            var snapshot = new GameObjectDeleteOperation()
                .Preflight(Context(inputs, GoIn, NoPorts)).Snapshot;
            // More than the target is gone: the sibling vanished too.
            UnityEngine.Object.DestroyImmediate(target);
            UnityEngine.Object.DestroyImmediate(sibling);

            var verdict = new GameObjectDeleteOperation().Reconcile(
                Context(inputs, GoIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "another missing child must never be Completed");
        }

        [Test]
        public void DeleteReconcile_PreexistingSameNameArtifact_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = Plain(harness, "DelSame");
            var inputs = new Dictionary<string, InputValue>
            {
                ["target"] = InputValue.Present(GoRef(target)),
            };
            var snapshot = new GameObjectDeleteOperation()
                .Preflight(Context(inputs, GoIn, NoPorts)).Snapshot;
            UnityEngine.Object.DestroyImmediate(target);
            // A foreign same-name object takes the target's place: it is a
            // different identity, never the deleted artifact's survival.
            Plain(harness, "DelSame");

            var verdict = new GameObjectDeleteOperation().Reconcile(
                Context(inputs, GoIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>());
        }

        [Test]
        public void DeleteReconcile_MovedArtifact_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = Plain(harness, "DelMoved");
            var other = Plain(harness, "DelNewParent");
            var inputs = new Dictionary<string, InputValue>
            {
                ["target"] = InputValue.Present(GoRef(target)),
            };
            var snapshot = new GameObjectDeleteOperation()
                .Preflight(Context(inputs, GoIn, NoPorts)).Snapshot;
            // The artifact was not deleted but moved under another parent.
            target.transform.SetParent(other.transform, false);

            var verdict = new GameObjectDeleteOperation().Reconcile(
                Context(inputs, GoIn, NoPorts), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "a moved target must never be Completed or mis-deleted");
            Assert.That(target, Is.Not.Null);
        }

        [Test]
        public void DeleteReconcile_SimulatedRestart_ReturnsUnknown()
        {
            using var harness = OperationTestHarness.Create();
            var target = Plain(harness, "DelRestart");
            var inputs = new Dictionary<string, InputValue>
            {
                ["target"] = InputValue.Present(GoRef(target)),
            };
            var snapshot = new GameObjectDeleteOperation()
                .Preflight(Context(inputs, GoIn, NoPorts)).Snapshot;
            var ran = new GameObjectDeleteOperation().Execute(Context(inputs, GoIn, NoPorts));
            Assert.That(ran.Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new GameObjectDeleteOperation().Reconcile(
                Context(inputs, GoIn, NoPorts), Restarted(snapshot));

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>());
        }

        // ── domain reload (SPEC §T.3) ───────────────────────────────────

        private static bool CleanupReloadArtifacts()
        {
            var dirty = false;
            // When no domain reload happened, the abandoned harness is still
            // registered: its own Dispose runs the ordered scene restore,
            // object destruction, folder deletion and registry removal. A
            // stale entry (disposed already, or the field outliving its
            // run) is cleared either way.
            if (s_reloadHarness is { } abandoned)
            {
                s_reloadHarness = null;
                try { abandoned.Dispose(); }
                catch (Exception) { /* cleanup is best-effort */ }
            }
            s_domainSentinel = null;
            // The [UnityTest]'s harness is abandoned mid-run: the domain
            // reload destroys the in-memory owner, so the harness's saved
            // scenes stay loaded and its folder stays on disk under
            // Assets/OpWeaveTesting — residue the next harness fixture would
            // trip on. One fresh single scene replaces whatever of it is
            // still open (a scene cannot be closed while it is the only one
            // loaded); the root folder then goes the same way
            // OperationTestHarnessTests.TearDown drops a leftover.
            var stray = false;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded &&
                    scene.path.StartsWith(
                        HarnessRoot + "/", StringComparison.Ordinal))
                {
                    stray = true;
                    break;
                }
            }
            if (stray)
            {
                EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
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

        // Phase 1 (pre-reload): run both Reconcilable Operations inside the
        // harness's saved scene and persist each Preflight snapshot as JSON
        // — the same evidence a StepStarted journal record would hold.
        private static string RunPhaseBeforeReload()
        {
            try
            {
                var harness = s_reloadHarness = OperationTestHarness.Create();
                HarnessObjects.NewGameObject(harness, "ReloadMarker");
                var scene = SceneManager.GetSceneByPath(harness.TempScenePath);

                var createInputs = CreateInputs("ReloadCreated", "cube");
                createInputs["scene"] = InputValue.Present(SceneRef(scene));
                var createSnapshot = new GameObjectCreateOperation()
                    .Preflight(Context(createInputs, CreateIn, GoOut)).Snapshot;
                var ran = new GameObjectCreateOperation()
                    .Execute(Context(createInputs, CreateIn, GoOut));
                if (ran.Kind != StepResultKind.Succeeded)
                {
                    return "create execute failed: " + (ran.Error?.Message ?? "?");
                }

                var doomed = Plain(harness, "ReloadDeleted");
                SceneManager.MoveGameObjectToScene(doomed, scene);
                var deleteInputs = new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(GoRef(doomed)),
                };
                var deleteSnapshot = new GameObjectDeleteOperation()
                    .Preflight(Context(deleteInputs, GoIn, NoPorts)).Snapshot;
                var deleted = new GameObjectDeleteOperation()
                    .Execute(Context(deleteInputs, GoIn, NoPorts));
                if (deleted.Kind != StepResultKind.Succeeded)
                {
                    return "delete execute failed: " + (deleted.Error?.Message ?? "?");
                }

                Directory.CreateDirectory(ProbeDir);
                var token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
                File.WriteAllText(
                    ProbeFile,
                    scene.path + "\n" +
                    createSnapshot.CanonicalText + "\n" +
                    deleteSnapshot.CanonicalText + "\n" +
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

        // Phase 2 (post-reload): rebuild the step contexts and reconcile the
        // persisted snapshots — same session, handles alive, so both verdicts
        // must be Completed with the uninterrupted-run outputs.
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
                if (expectedToken.Length == 0 || lines.Length < 4 ||
                    lines[3] != expectedToken)
                {
                    return "the persisted probe state is stale — a previous " +
                        "Editor session wrote " + ProbeFile;
                }
                var scene = SceneManager.GetSceneByPath(lines[0]);
                if (!scene.isLoaded)
                {
                    return "the probe scene is not loaded after the reload";
                }
                var createInputs = CreateInputs("ReloadCreated", "cube");
                createInputs["scene"] = InputValue.Present(SceneRef(scene));
                var createVerdict = new GameObjectCreateOperation().Reconcile(
                    Context(createInputs, CreateIn, GoOut), JsonValue.Parse(lines[1]).Value!);
                LogEvidence(GameObject.Find("ReloadCreated"));
                if (createVerdict is not ReconcileResult.Completed completed)
                {
                    return "create reconcile was not Completed: " + VerdictText(createVerdict);
                }
                var resolved = new RefResolver().Resolve<GameObject>(
                    completed.Outputs["gameObject"]);
                if (!resolved.Ok || resolved.Value!.name != "ReloadCreated")
                {
                    return "the create output does not name the created object";
                }
                var deleteInputs = new Dictionary<string, InputValue>();
                var deleteVerdict = new GameObjectDeleteOperation().Reconcile(
                    Context(deleteInputs, GoIn, NoPorts), JsonValue.Parse(lines[2]).Value!);
                if (deleteVerdict is not ReconcileResult.Completed)
                {
                    return "delete reconcile was not Completed: " + VerdictText(deleteVerdict);
                }
                return string.Empty;
            }
            catch (Exception e)
            {
                return "phase-2 threw " + e.GetType().Name + ": " + e.Message;
            }
        }

        private static string VerdictText(ReconcileResult verdict) =>
            verdict is ReconcileResult.Unknown unknown
                ? "Unknown(" + unknown.Reason + ")"
                : verdict.GetType().Name;

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
