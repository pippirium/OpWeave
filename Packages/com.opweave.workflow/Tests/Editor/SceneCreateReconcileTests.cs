#nullable enable

// T-W2.5-01 [Edit] tests: local:T-W2.5-01:scene-create-reconcile — the six
// SPEC §F5 Operation-level cases for unity.scene.create: not executed;
// executed but StepSucceeded not recorded; partial side effect;
// pre-existing same-name artifact; artifact modified by another operation;
// simulated Editor restart — plus one [UnityTest] crossing a real domain
// reload (RecompileScripts, SPEC §T.3), the version-2 default-content
// expectation (observe-only Reconcile; a real saved-and-reloaded default
// scene pins the per-root PrefabContentFingerprint v1 table — selected
// per Unity major.minor, T-W2.5-06 — always logging the computed values
// on a "[W2501-golden]" line, and an editor without a pinned table
// yields no fingerprint member and Unknown), the untitled-scene
// fail-closed cases and the additive untitled-scene Execute refusal
// (OW-UNITY-011). Reconcile returns Completed only with the full
// evidence row, NotApplied only when provable, else Unknown; no case
// duplicates or mis-deletes an artifact.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.5-01:scene-create-reconcile — the §F5 evidence row and the
    /// six interruption cases of <c>unity.scene.create</c>.
    /// </summary>
    internal sealed class SceneCreateReconcileTests
    {
        private const string ProbeFolder = "Assets/OpWeaveSceneReconcile";
        private const string BaseScenePath = ProbeFolder + "/Base.unity";
        private const string MarkerFolder = "Assets/OpWeaveSceneReconcileMarker";
        private const string TargetPath = ProbeFolder + "/Target.unity";
        private const string GoldenScenePath = ProbeFolder + "/GoldenReference.unity";

        private static readonly IReadOnlyDictionary<string, string> InputTypes =
            new Dictionary<string, string>
            {
                ["path"] = "unity.AssetPath",
                ["setup"] = "core.String",
                ["mode"] = "core.String",
            };

        private static readonly IReadOnlyDictionary<string, string> OutputTypes =
            new Dictionary<string, string> { ["scene"] = "unity.SceneRef" };

        // [UnitySetUp]/[UnityTearDown], never plain [SetUp]/[TearDown]: UTF
        // does not persist plain SetUp/TearDown state across a domain
        // reload, so on the resume inside the [UnityTest] below a [SetUp]
        // would run a second time and its Single-mode NewScene would close
        // the loaded Target scene the reload is meant to leave untouched.
        // The Unity pair's before/after state survives the reload, so the
        // already-run before-actions are skipped on resume.
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            EnsureFolder(ProbeFolder);
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, BaseScenePath), Is.True,
                "the base scene must save");
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (var folder in new[] { ProbeFolder, MarkerFolder })
            {
                if (AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                }
                var disk = Path.Combine(
                    ProjectRoot(), folder.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(disk))
                {
                    try { Directory.Delete(disk, recursive: true); } catch (Exception) { }
                }
                if (File.Exists(disk + ".meta"))
                {
                    try { File.Delete(disk + ".meta"); } catch (Exception) { }
                }
            }
            if (Directory.Exists(ProbeStateDir))
            {
                try { Directory.Delete(ProbeStateDir, recursive: true); } catch (Exception) { }
            }
            AssetDatabase.Refresh();
            yield break;
        }

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));

        private static string ProbeStateDir =>
            Path.Combine(ProjectRoot(), "Library", "OpWeaveSceneReconcileProbe");

        private static string SnapshotStatePath =>
            Path.Combine(ProbeStateDir, "snapshot.txt");

        // ── §F5 snapshot evidence ────────────────────────────────────────

        // The §F5 evidence row: path and its nothing-there state, mode and
        // setup, the expected root configuration, the loaded scene list, the
        // active scene and the session id.
        [Test]
        public void Preflight_Snapshot_CarriesTheEvidenceRow()
        {
            var preflight = new CreateSceneOperation().Preflight(
                Context(Inputs(("path", TargetPath), ("setup", "empty"))));

            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((string?)snapshot["path"], Is.EqualTo(TargetPath));
            Assert.That((bool?)snapshot["pathExisted"], Is.False);
            Assert.That((string?)snapshot["mode"], Is.EqualTo("additive"));
            Assert.That((string?)snapshot["setup"], Is.EqualTo("empty"));
            Assert.That(snapshot["expectedRoots"], Is.Not.Null);
            Assert.That(((JArray)snapshot["expectedRoots"]!).Count, Is.EqualTo(0));
            Assert.That((int?)snapshot["expectedRootsVersion"],
                Is.EqualTo(SceneOps.ExpectedRootsVersion));
            var loaded = (JArray)snapshot["loadedScenes"]!;
            Assert.That(loaded.Count, Is.EqualTo(1));
            Assert.That((string?)loaded[0]["path"], Is.EqualTo(BaseScenePath));
            Assert.That((string?)snapshot["activeScene"]!["path"], Is.EqualTo(BaseScenePath));
            Assert.That((string?)snapshot["editorSessionId"], Is.Not.Empty);
        }

        // ── the six §F5 cases ────────────────────────────────────────────

        // Case 1 — interrupted before Execute: nothing at the target and
        // the loaded list unchanged is provably not applied.
        [Test]
        public void Reconcile_NotExecuted_ReturnsNotApplied()
        {
            var snapshot = PreflightSnapshot(TargetPath);

            var verdict = new CreateSceneOperation().Reconcile(
                Context(Inputs(("path", TargetPath))), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 2 — interrupted after Execute before StepSucceeded: the new
        // scene asset has the expected roots and is loaded additively as
        // the active scene — EditorSceneManager.NewScene makes the new
        // scene active — so the verdict completes with the uninterrupted
        // run's outputs.
        [Test]
        public void Reconcile_ExecutedUnrecorded_ReturnsCompletedWithOutputs()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            var context = Context(inputs);
            Assert.That(
                new CreateSceneOperation().Execute(context).Kind,
                Is.EqualTo(StepResultKind.Succeeded),
                "the uninterrupted run must have produced the artifact");
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(TargetPath),
                "an additive create leaves the created scene active");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            var scene = (JObject)outputs["scene"].ToToken();
            Assert.That((string?)scene["kind"], Is.EqualTo("scene"));
            Assert.That((string?)scene["path"], Is.EqualTo(TargetPath));
            Assert.That((string?)scene["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(TargetPath)));
            Assert.That(
                JToken.DeepEquals(scene, (JObject)context.Outputs["scene"].ToToken()),
                Is.True, "restored outputs equal the uninterrupted run's");
        }

        // Case 3 — partial side effect: the scene file exists but is not
        // loaded, which is neither the Completed the evidence allows nor a
        // provable NotApplied — Unknown, NeedsReview, never re-run.
        [Test]
        public void Reconcile_PartialSideEffect_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var scene = SceneManager.GetSceneByPath(TargetPath);
            Assert.That(EditorSceneManager.CloseScene(scene, true), Is.True,
                "the fixture must unload the artifact while leaving its file");
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(TargetPath), Is.Not.Null);

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "not loaded");
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(TargetPath), Is.Not.Null,
                "an Unknown verdict never deletes the artifact it could not prove");
        }

        // A foreign loaded scene in the loaded set beside an empty target is
        // Unknown too: NotApplied needs the whole loaded list unchanged.
        [Test]
        public void Reconcile_EmptyPathButChangedLoadedSet_ReturnsUnknown()
        {
            var snapshot = PreflightSnapshot(TargetPath);
            var extra = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.That(EditorSceneManager.SaveScene(extra, ProbeFolder + "/Extra.unity"),
                Is.True, "the fixture must add a loaded scene");

            var verdict = new CreateSceneOperation().Reconcile(
                Context(Inputs(("path", TargetPath))), snapshot);

            AssertUnknown(verdict, "loaded scene set differs");
        }

        // Case 4 — a pre-existing same-name artifact: the snapshot recorded
        // it, so nothing at the path can be attributed to the step.
        [Test]
        public void Reconcile_PreexistingArtifact_ReturnsUnknown()
        {
            var snapshot = (JObject)PreflightSnapshot(TargetPath).ToToken();
            var foreign = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.That(EditorSceneManager.SaveScene(foreign, TargetPath), Is.True,
                "the fixture must place a foreign artifact at the path");
            snapshot["pathExisted"] = true;

            var verdict = new CreateSceneOperation().Reconcile(
                Context(Inputs(("path", TargetPath))), JsonValue.From(snapshot));

            AssertUnknown(verdict, "pre-existing");
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(TargetPath), Is.Not.Null,
                "the pre-existing artifact is never deleted or claimed");
        }

        // Case 5 — the artifact was changed by another operation after the
        // step ran: the root configuration no longer matches the snapshot's
        // expected setup.
        [Test]
        public void Reconcile_ModifiedArtifact_ReturnsUnknown_AndChangesNothing()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var scene = SceneManager.GetSceneByPath(TargetPath);
            var added = new UnityEngine.GameObject("SomeoneElsesRoot");
            SceneManager.MoveGameObjectToScene(added, scene);
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True,
                "the fixture must persist the foreign modification");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "root");
            Assert.That(added, Is.Not.Null,
                "a modified artifact is left untouched");
            Assert.That(SceneManager.GetSceneByPath(TargetPath).rootCount, Is.EqualTo(1),
                "no duplicate or deleted artifact");
        }

        // ── §F5 content evidence and loaded-state regression tests ─────

        // The matching Completed for the default setup: a fresh Execute
        // leaves roots carrying exactly the components this Editor's
        // default setup produces, so the evidence row proves Completed.
        [Test]
        public void Reconcile_DefaultSetupIntact_ReturnsCompleted()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "default"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.That((string?)((JObject)outputs["scene"].ToToken())["path"],
                Is.EqualTo(TargetPath));
        }

        // The same default setup with the Camera removed from Main
        // Camera: the root names still match but the component evidence
        // does not — Unknown, never Completed.
        [Test]
        public void Reconcile_DefaultRootComponentRemoved_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "default"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var scene = SceneManager.GetSceneByPath(TargetPath);
            var camera = scene.GetRootGameObjects()
                .Single(root => root.name == "Main Camera")
                .GetComponent<UnityEngine.Camera>();
            Assert.That(camera, Is.Not.Null,
                "the default setup must place a Camera on Main Camera");
            UnityEngine.Object.DestroyImmediate(camera);
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True,
                "the fixture must persist the modification");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "component set");
        }

        // A changed component value that keeps every structural property:
        // the Camera's field of view differs from what the default setup
        // produces while names, states, children and component types all
        // still match — the content evidence catches it: Unknown, never
        // Completed.
        [Test]
        public void Reconcile_DefaultComponentValueChanged_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "default"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var scene = SceneManager.GetSceneByPath(TargetPath);
            var camera = scene.GetRootGameObjects()
                .Single(root => root.name == "Main Camera")
                .GetComponent<UnityEngine.Camera>();
            Assert.That(camera, Is.Not.Null,
                "the default setup must place a Camera on Main Camera");
            camera.fieldOfView += 20f;
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True,
                "the fixture must persist the modification");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "content");
        }

        // The same for a transform value: a moved root keeps the name,
        // the active flag, the child count and the component set — only
        // the value-level proof sees the difference: Unknown, never
        // Completed.
        [Test]
        public void Reconcile_DefaultRootMoved_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "default"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var scene = SceneManager.GetSceneByPath(TargetPath);
            var camera = scene.GetRootGameObjects()
                .Single(root => root.name == "Main Camera");
            camera.transform.position =
                camera.transform.position + new UnityEngine.Vector3(1f, 0f, 0f);
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True,
                "the fixture must persist the modification");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "content");
        }

        // A saved change that no property subset ever compared:
        // disabling the Main Camera GameObject keeps its component set
        // and every compared value identical, but the root's state — and
        // the fingerprinted content — is no longer the expected setup:
        // Unknown, never Completed.
        [Test]
        public void Reconcile_DefaultRootDisabled_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "default"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var scene = SceneManager.GetSceneByPath(TargetPath);
            scene.GetRootGameObjects()
                .Single(root => root.name == "Main Camera")
                .SetActive(false);
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True,
                "the fixture must persist the disabled root");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "state");
        }

        // A saved change to a serialized property the property-subset
        // expectation never compared — the Light's bounce intensity —
        // keeps the root structure and the component set identical, so
        // only the complete-content fingerprint can prove the artifact
        // different: Unknown, never Completed.
        [Test]
        public void Reconcile_DefaultUncheckedPropertyChanged_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "default"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var scene = SceneManager.GetSceneByPath(TargetPath);
            var light = scene.GetRootGameObjects()
                .Single(root => root.name == "Directional Light")
                .GetComponent<UnityEngine.Light>();
            Assert.That(light, Is.Not.Null,
                "the default setup must place a Light on Directional Light");
            light.bounceIntensity += 0.5f;
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True,
                "the fixture must persist the modification");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "content");
        }

        // Reconcile is observe-only: deciding the intact default setup
        // must not change the Editor — same scene count, same loaded
        // list, same active scene and not one isDirty flag flipped.
        [Test]
        public void Reconcile_DefaultSetupIntact_DoesNotMutateEditor()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "default"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var countBefore = SceneManager.sceneCount;
            var loadedBefore = LoadedSceneStates();
            var activeBefore = SceneManager.GetActiveScene().handle;

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "the intact default state must complete for the no-mutation check to mean anything");
            Assert.That(SceneManager.sceneCount, Is.EqualTo(countBefore));
            Assert.That(LoadedSceneStates(), Is.EqualTo(loadedBefore));
            Assert.That(SceneManager.GetActiveScene().handle, Is.EqualTo(activeBefore));
        }

        // An extra root — or a child under a default root — changes the
        // root configuration the setup produces: Unknown, never
        // Completed.
        [Test]
        public void Reconcile_DefaultExtraRootOrChild_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "default"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var scene = SceneManager.GetSceneByPath(TargetPath);

            var extra = new UnityEngine.GameObject("ExtraRoot");
            SceneManager.MoveGameObjectToScene(extra, scene);
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True,
                "the fixture must persist the extra root");
            AssertUnknown(
                new CreateSceneOperation().Reconcile(Context(inputs), snapshot), "root");

            UnityEngine.Object.DestroyImmediate(extra);
            var child = new UnityEngine.GameObject("Child");
            SceneManager.MoveGameObjectToScene(child, scene);
            child.transform.SetParent(
                scene.GetRootGameObjects()
                    .Single(root => root.name == "Main Camera").transform);
            Assert.That(EditorSceneManager.SaveScene(scene), Is.True,
                "the fixture must persist the child");
            AssertUnknown(
                new CreateSceneOperation().Reconcile(Context(inputs), snapshot), "children");
        }

        // The version-2 expectation pins the built-in pipeline's default
        // content: the PrefabContentFingerprint v1 of every default root
        // of a real saved-and-reloaded NewSceneSetup.DefaultGameObjects
        // scene — the persisted form Reconcile actually compares — must
        // equal the pinned table, so a Unity default change fails loudly
        // instead of silently weakening the row. The computed values are
        // always logged on one "[W2501-golden]" line for re-pinning.
        [Test]
        public void ExpectedRootsV2_EqualsRealDefaultScene()
        {
            if (UnityEngine.Rendering.RenderPipelineManager.currentPipeline is not null)
            {
                Assert.Ignore(
                    "the version-2 fingerprint expectation pins the built-in pipeline's defaults");
            }
            var reference = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
            try
            {
                Assert.That(
                    EditorSceneManager.SaveScene(reference, GoldenScenePath), Is.True,
                    "the fixture must save the real default scene");
                Assert.That(EditorSceneManager.CloseScene(reference, true), Is.True,
                    "the fixture must unload the saved scene");
                var reloaded = EditorSceneManager.OpenScene(
                    GoldenScenePath, OpenSceneMode.Additive);
                try
                {
                    var roots = reloaded.GetRootGameObjects();
                    var fingerprints = new string?[roots.Length];
                    var line = "[W2501-golden]";
                    for (var i = 0; i < roots.Length; i++)
                    {
                        var computed = PrefabContentFingerprint.Compute(roots[i]);
                        fingerprints[i] = computed is PrefabFingerprintResult.Computed c
                            ? c.Fingerprint.Hash
                            : null;
                        line += " " + roots[i].name + "=" +
                            (fingerprints[i] ??
                                "<" + ((PrefabFingerprintResult.Unknown)computed).Reason + ">");
                    }
                    UnityEngine.Debug.Log(line);

                    var expected = SceneOps.ExpectedRoots(SceneOps.SetupDefault);
                    Assert.That(roots.Length, Is.EqualTo(expected.Count),
                        "the default setup's root count");
                    for (var i = 0; i < roots.Length; i++)
                    {
                        var entry = (JObject)expected[i]!;
                        Assert.That(roots[i].name, Is.EqualTo((string?)entry["name"]));
                        Assert.That(fingerprints[i], Is.Not.Null,
                            "a default root must be fingerprintable");
                        Assert.That(entry["fingerprint"], Is.TypeOf<JValue>(),
                            "the pinned table must record a fingerprint for every default root");
                        Assert.That(fingerprints[i],
                            Is.EqualTo((string?)entry["fingerprint"]),
                            "the persisted default scene must carry the pinned fingerprint");
                    }
                }
                finally
                {
                    EditorSceneManager.CloseScene(reloaded, true);
                }
            }
            finally
            {
                if (reference.isLoaded)
                {
                    EditorSceneManager.CloseScene(reference, true);
                }
            }
        }

        // T-W2.5-06: an editor without a pinned fingerprint table — a
        // Unity major.minor outside the pinned set — records no
        // fingerprint member in its expectedRoots, so the default
        // content is unprovable there and Reconcile is Unknown, never
        // Completed (§G6.1 comparability holds within one Unity version
        // only; a snapshot recorded on one editor is never judged with
        // another editor's table).
        [Test]
        public void Reconcile_UnpinnedEditorTable_ReturnsUnknown()
        {
            const string unpinned = "9999.9";
            Assert.That(SceneOps.DefaultRootFingerprints(unpinned), Is.Null,
                "a major.minor outside the pinned set must yield no table");
            var unpinnedRoots = SceneOps.ExpectedRoots(SceneOps.SetupDefault, unpinned);
            Assert.That(unpinnedRoots.Count, Is.EqualTo(2),
                "the unpinned expectation still describes every default root");
            foreach (var entry in unpinnedRoots)
            {
                Assert.That(((JObject)entry)["fingerprint"], Is.Null,
                    "an unpinned editor records no fingerprint member");
            }

            var inputs = Inputs(("path", TargetPath), ("setup", "default"));
            var snapshot = (JObject)new CreateSceneOperation()
                .Preflight(Context(inputs)).Snapshot.ToToken();
            snapshot["expectedRoots"] = unpinnedRoots;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new CreateSceneOperation().Reconcile(
                Context(inputs), JsonValue.From(snapshot));

            AssertUnknown(verdict, "content");
        }

        // The additive loaded state: a scene the snapshot recorded is
        // closed after Execute, so the loaded set is no longer the
        // recorded set plus the created scene — Unknown, never Completed.
        [Test]
        public void Reconcile_AdditiveLoadedSceneClosed_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var previous = SceneManager.GetSceneByPath(BaseScenePath);
            Assert.That(EditorSceneManager.CloseScene(previous, true), Is.True,
                "the fixture must unload a scene the snapshot recorded");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "loaded scene set");
        }

        // The additive active-scene half of the row: a scene other than
        // the created one made active after Execute is not the
        // uninterrupted state — Unknown, never Completed.
        [Test]
        public void Reconcile_AdditiveActiveSceneSwitched_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(
                SceneManager.SetActiveScene(SceneManager.GetSceneByPath(BaseScenePath)),
                Is.True, "the fixture must switch the active scene");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "active scene");
        }

        // An unsaved (Untitled) scene records an empty path, so a
        // path+name entry can never re-identify it after an
        // interruption — an empty-path entry in the recorded loaded set
        // is Unknown, never Completed. A real untitled scene can no
        // longer reach the recorded set — additive Preflight refuses it
        // outright — so the prepared snapshot injects the entry a
        // foreign or pre-refusal snapshot could carry.
        [Test]
        public void Reconcile_RecordedUntitledScene_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
            var snapshot = (JObject)new CreateSceneOperation()
                .Preflight(Context(inputs)).Snapshot.ToToken();
            ((JArray)snapshot["loadedScenes"]!).Add(
                new JObject { ["path"] = "", ["name"] = "Untitled" });
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new CreateSceneOperation().Reconcile(
                Context(inputs), JsonValue.From(snapshot));

            AssertUnknown(verdict, "untitled");
        }

        // The same for the current set: an untitled scene loaded after
        // Execute is an entry the row cannot re-identify — Unknown,
        // never Completed.
        [Test]
        public void Reconcile_LoadedUntitledScene_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var untitled = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.That(untitled.path, Is.Empty,
                "the fixture needs an untitled scene in the current set");

            var verdict = new CreateSceneOperation().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "untitled");
        }

        // And in the NotApplied comparison: with nothing at the path, an
        // untitled entry in the recorded set makes even an unchanged
        // loaded list unprovable — Unknown, never NotApplied.
        [Test]
        public void Reconcile_NotExecutedRecordedUntitled_ReturnsUnknown()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
            var snapshot = (JObject)new CreateSceneOperation()
                .Preflight(Context(inputs)).Snapshot.ToToken();
            ((JArray)snapshot["loadedScenes"]!).Add(
                new JObject { ["path"] = "", ["name"] = "Untitled" });

            var verdict = new CreateSceneOperation().Reconcile(
                Context(inputs), JsonValue.From(snapshot));

            AssertUnknown(verdict, "untitled");
        }

        // Additive Preflight and Execute refuse the state Unity itself
        // cannot perform — NewScene throws while an untitled scene is
        // unsaved — deterministically with OW-UNITY-011.
        [Test]
        public void Execute_AdditiveWithUntitledLoaded_FailsOwUnity011()
        {
            var untitled = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.That(untitled.path, Is.Empty,
                "the fixture needs an unsaved untitled scene loaded");

            var result = new CreateSceneOperation().Execute(
                Context(Inputs(("path", TargetPath), ("setup", "empty"))));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error?.Code, Is.EqualTo("OW-UNITY-011"));
        }

        // Case 6 — a simulated Editor restart (different editorSessionId,
        // discarded handles): the scene asset stays re-identifiable by its
        // permanent path/guid/content keys, so the verdict still follows
        // the evidence (§F5 common rule 3).
        [Test]
        public void Reconcile_SimulatedRestart_StillDecidesByEvidence()
        {
            var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
            var snapshot = new CreateSceneOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateSceneOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var restarted = (JObject)snapshot.ToToken();
            restarted["editorSessionId"] = "00000000000000000000000000c0ffee";
            var restartSnapshot = JsonValue.From(restarted);

            var verdict = new CreateSceneOperation().Reconcile(
                Context(inputs), restartSnapshot);
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "a file artifact is re-identifiable across a session boundary");

            var scene = SceneManager.GetSceneByPath(TargetPath);
            Assert.That(EditorSceneManager.CloseScene(scene, true), Is.True);
            Assert.That(AssetDatabase.DeleteAsset(TargetPath), Is.True,
                "the fixture must delete the probe asset");
            AssetDatabase.Refresh();
            var absent = new CreateSceneOperation().Reconcile(
                Context(inputs), restartSnapshot);
            Assert.That(absent, Is.TypeOf<ReconcileResult.NotApplied>(),
                "an absent artifact stays safely re-runnable after a restart");
        }

        // Evidence that cannot be re-established is Unknown — never
        // Completed, never NotApplied.
        [Test]
        public void Reconcile_MalformedOrUnestablishedEvidence_ReturnsUnknown()
        {
            var snapshot = PreflightSnapshot(TargetPath);

            Assert.That(
                new CreateSceneOperation().Reconcile(
                    Context(Inputs(("path", TargetPath))), JsonValue.From(new JValue(5))),
                Is.TypeOf<ReconcileResult.Unknown>(),
                "a non-object snapshot restores no evidence");

            var missing = (JObject)snapshot.ToToken();
            missing.Remove("expectedRoots");
            AssertUnknown(
                new CreateSceneOperation().Reconcile(
                    Context(Inputs(("path", TargetPath))), JsonValue.From(missing)),
                "missing");

            var badRoots = (JObject)snapshot.ToToken();
            badRoots["expectedRoots"] = new JArray(5);
            AssertUnknown(
                new CreateSceneOperation().Reconcile(
                    Context(Inputs(("path", TargetPath))), JsonValue.From(badRoots)),
                "malformed");

            var noVersion = (JObject)PreflightSnapshot(TargetPath).ToToken();
            noVersion.Remove("expectedRootsVersion");
            AssertUnknown(
                new CreateSceneOperation().Reconcile(
                    Context(Inputs(("path", TargetPath))), JsonValue.From(noVersion)),
                "missing");

            var badVersion = (JObject)PreflightSnapshot(TargetPath).ToToken();
            badVersion["expectedRootsVersion"] = 0;
            AssertUnknown(
                new CreateSceneOperation().Reconcile(
                    Context(Inputs(("path", TargetPath))), JsonValue.From(badVersion)),
                "version");
        }

        // ── [UnityTest] across a real domain reload (SPEC §T.3) ──────────

        private static object? s_domainSentinel;
        private static void ArmSentinel() => s_domainSentinel = new object();
        private static bool SentinelAlive => s_domainSentinel is not null;

        private const string MarkerSource =
            "internal static class SceneReconcileReloadMarker { }\n";

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;
            public static PhaseResult Pass() => new PhaseResult { Ok = true };
            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        // Executes the step, persists the Preflight snapshot under Library/
        // (UTF locals do not survive the reload), then writes the marker
        // script whose import forces the domain reload.
        private static PhaseResult PhaseBeforeReload()
        {
            try
            {
                var inputs = Inputs(("path", TargetPath), ("setup", "empty"));
                var preflight = new CreateSceneOperation().Preflight(Context(inputs));
                if (preflight.Diagnostics.Count != 0)
                {
                    return PhaseResult.Fail(
                        "preflight diagnostics: " + string.Join("; ",
                            preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
                }
                var context = Context(inputs);
                var executed = new CreateSceneOperation().Execute(context);
                if (executed.Kind != StepResultKind.Succeeded)
                {
                    return PhaseResult.Fail(
                        "execute failed: " +
                        (executed.Error?.Code + " " + executed.Error?.Message));
                }
                Directory.CreateDirectory(ProbeStateDir);
                File.WriteAllText(SnapshotStatePath, preflight.Snapshot.CanonicalText);

                var markerDisk = Path.Combine(ProjectRoot(), MarkerFolder);
                Directory.CreateDirectory(markerDisk);
                File.WriteAllText(
                    Path.Combine(markerDisk, "SceneReconcileReloadMarker.cs"), MarkerSource);
                AssetDatabase.Refresh();
                ArmSentinel();
                LogLoadedScenes("pre-reload");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-1 threw: " + e.Message);
            }
        }

        // After the reload: re-read the persisted snapshot (nothing is
        // carried in memory) and reconcile — the created scene asset is
        // still at its path, loaded, with the expected roots, so the
        // verdict is Completed with the uninterrupted outputs restored.
        private static PhaseResult PhaseAfterReload()
        {
            try
            {
                LogLoadedScenes("post-reload");
                var parsed = JsonValue.Parse(File.ReadAllText(SnapshotStatePath));
                if (!parsed.Ok || parsed.Value is null)
                {
                    return PhaseResult.Fail("persisted snapshot did not parse");
                }
                var verdict = new CreateSceneOperation().Reconcile(
                    Context(Inputs(("path", TargetPath), ("setup", "empty"))),
                    parsed.Value);
                if (verdict is not ReconcileResult.Completed completed)
                {
                    return PhaseResult.Fail(
                        "expected Completed, got " +
                        (verdict is ReconcileResult.Unknown unknown
                            ? "Unknown(" + unknown.Reason + ")"
                            : verdict.GetType().Name));
                }
                var scene = (JObject)completed.Outputs["scene"].ToToken();
                if ((string?)scene["path"] != TargetPath ||
                    (string?)scene["guid"] != AssetDatabase.AssetPathToGUID(TargetPath))
                {
                    return PhaseResult.Fail(
                        "restored outputs do not match the uninterrupted run");
                }
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-2 threw: " + e.Message);
            }
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Reconcile_AfterRealDomainReload_ReturnsCompleted()
        {
            var before = PhaseBeforeReload();
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            yield return new RecompileScripts();
            var guard = 0;
            while (SentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(SentinelAlive,
                "the marker compile did not produce a domain reload");

            var after = PhaseAfterReload();
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // ── fixtures ─────────────────────────────────────────────────────

        // The reload probe's own state dump: every SceneManager scene with
        // its path and isLoaded flag plus the active scene — the evidence a
        // failing run needs to tell an unloaded scene from a path mismatch.
        private static void LogLoadedScenes(string phase)
        {
            var line = "[W2501-reload] " + phase + " loaded scenes:";
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                line += " (path=" + scene.path + " isLoaded=" + scene.isLoaded + ")";
            }
            var active = SceneManager.GetActiveScene();
            line += " | active scene: path=" + active.path +
                " isLoaded=" + active.isLoaded;
            UnityEngine.Debug.Log(line);
        }

        private static Dictionary<string, InputValue> Inputs(
            params (string Port, string Value)[] values)
        {
            var inputs = new Dictionary<string, InputValue>();
            foreach (var (port, value) in values)
            {
                inputs[port] = InputValue.Present(Json("\"" + value + "\""));
            }
            return inputs;
        }

        private static JsonValue PreflightSnapshot(string path)
        {
            var preflight = new CreateSceneOperation().Preflight(
                Context(Inputs(("path", path))));
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => "preflight must produce the snapshot: " + string.Join("; ",
                    preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return preflight.Snapshot;
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs) =>
            new UnityStepContext(
                inputs,
                InputTypes,
                OutputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "create"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

        private static void AssertUnknown(ReconcileResult verdict, string fragment)
        {
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => verdict is ReconcileResult.Unknown u ? u.Reason : verdict.GetType().Name);
            Assert.That(((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain(fragment));
        }

        // The mutation evidence of Reconcile_DefaultSetupIntact_DoesNot-
        // MutateEditor: every loaded scene's identity and dirty flag,
        // captured before and after the verdict.
        private static List<string> LoadedSceneStates()
        {
            var states = new List<string>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                states.Add(
                    scene.handle + "|" + scene.path + "|" + scene.name + "|" +
                    scene.isLoaded + "|" + scene.isDirty);
            }
            return states;
        }

        private static void EnsureFolder(string folderPath)
        {
            var segments = folderPath.Split('/');
            var parent = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var child = parent + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(child))
                {
                    Assert.That(AssetDatabase.CreateFolder(parent, segments[i]),
                        Is.Not.Empty, "the probe folder must be created: " + child);
                }
                parent = child;
            }
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }
    }
}
