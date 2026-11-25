#nullable enable

// T-W2.5-01 [Edit] tests: local:T-W2.5-01:scene-ops — the SPEC §G5 triad
// (normal, Undo, precondition violation) for unity.scene.open,
// unity.scene.create, unity.scene.save and unity.scene.close (§I.1):
// dirty scenes with fail refuse OW-UNITY-011, an existing create path is
// OW-UNITY-005, an Untitled save without path is refused, closing the last
// scene is refused, and closing an already closed scene succeeds.
using System;
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

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.5-01:scene-ops — the §G5 normal / Undo / precondition
    /// triad of the four Scene Operations.
    /// </summary>
    internal sealed class SceneOperationTests
    {
        private const string ProbeFolder = "Assets/OpWeaveSceneProbe";
        private const string BaseScenePath = ProbeFolder + "/Base.unity";

        private static readonly IReadOnlyDictionary<string, string> NoOutputs =
            new Dictionary<string, string>();

        private static readonly IReadOnlyDictionary<string, string> SceneOut =
            new Dictionary<string, string> { ["scene"] = "unity.SceneRef" };

        [SetUp]
        public void SetUp()
        {
            EnsureFolder(ProbeFolder);
            // One clean saved scene is the deterministic starting state:
            // an untitled scene would be an extra dirty candidate, and a
            // test scene another test left behind would skew the loaded set.
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, BaseScenePath), Is.True,
                "the base scene must save");
        }

        [TearDown]
        public void TearDown()
        {
            // A fresh untitled single scene discards whatever the test left
            // loaded (test scenes inside the probe folder are disposable);
            // then the folder goes, file system and .meta included.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            var disk = Path.Combine(
                ProjectRoot(), ProbeFolder.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(disk))
            {
                try { Directory.Delete(disk, recursive: true); } catch (Exception) { }
            }
            if (File.Exists(disk + ".meta"))
            {
                try { File.Delete(disk + ".meta"); } catch (Exception) { }
            }
            AssetDatabase.Refresh();
        }

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));

        // ── unity.scene.open ─────────────────────────────────────────────

        [Test]
        public void Open_Additive_LoadsSceneAndOutputsSceneRef()
        {
            var path = CreateSavedScene(ProbeFolder + "/Other.unity");
            var changes = new StepChangeRecorder(0, "open");
            var context = Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", path), ("mode", "additive")),
                SceneOut, changes);

            var result = new OpenSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var scene = SceneManager.GetSceneByPath(path);
            Assert.That(scene.isLoaded, Is.True, "the scene must be loaded additively");
            Assert.That(SceneManager.sceneCount, Is.EqualTo(2));
            AssertSceneRef(context.Outputs["scene"], path);
        }

        [Test]
        public void Open_Single_ReplacesLoadedScenes()
        {
            var other = CreateSavedScene(ProbeFolder + "/Other.unity");
            var kept = CreateSavedScene(ProbeFolder + "/Kept.unity");
            var context = Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", kept)),
                SceneOut);

            var result = new OpenSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(SceneManager.GetSceneByPath(other).isLoaded, Is.False);
            Assert.That(SceneManager.GetSceneByPath(kept).isLoaded, Is.True);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(1));
        }

        [Test]
        public void Open_AlreadyLoaded_IsANoOpSuccess()
        {
            var context = Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", BaseScenePath), ("mode", "single")),
                SceneOut);
            var dirtyBefore = SceneManager.GetSceneByPath(BaseScenePath).isDirty;

            var result = new OpenSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(SceneManager.sceneCount, Is.EqualTo(1));
            Assert.That(SceneManager.GetSceneByPath(BaseScenePath).isDirty,
                Is.EqualTo(dirtyBefore), "a no-op must not dirty the scene");
            AssertSceneRef(context.Outputs["scene"], BaseScenePath);
        }

        [Test]
        public void Open_SingleWithDirtyScenes_FailsOwUnity011()
        {
            var path = CreateSavedScene(ProbeFolder + "/Other.unity");
            EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneByPath(BaseScenePath));
            var context = Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", path), ("mode", "single")),
                SceneOut);

            var result = new OpenSceneOperation().Execute(context);

            AssertFailedWith(result, ErrorCodes.OW_UNITY_011, "dirtyScenes");
            Assert.That(SceneManager.GetSceneByPath(path).isLoaded, Is.False,
                "a refused open loads nothing");

            var preflight = new OpenSceneOperation().Preflight(Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", path), ("mode", "single")),
                SceneOut));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_011),
                "Preflight reports the same refusal (Dry Run truth)");
        }

        [Test]
        public void Open_SingleWithDirtyScenes_Save_SavesThenOpens()
        {
            var path = CreateSavedScene(ProbeFolder + "/Other.unity");
            EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneByPath(BaseScenePath));
            var changes = new StepChangeRecorder(0, "open");
            var context = Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", path), ("mode", "single"), ("dirtyScenes", "save")),
                SceneOut, changes);

            var result = new OpenSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(SceneManager.GetSceneByPath(BaseScenePath).isLoaded, Is.False);
            Assert.That(SceneManager.GetSceneByPath(path).isLoaded, Is.True);
            Assert.That(changes.Records.Any(r => r.Action == ChangeAction.Saved), Is.True,
                "the dirtyScenes=save write is a recorded change");
            Assert.That(changes.Records.All(r => !r.Undoable), Is.True,
                "§I.1 declares Undo None");
        }

        [Test]
        public void Open_MissingSceneAsset_FailsOwUnity005()
        {
            var result = new OpenSceneOperation().Execute(Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", ProbeFolder + "/Missing.unity")),
                SceneOut));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
        }

        // ── unity.scene.create ───────────────────────────────────────────

        [Test]
        public void Create_AdditiveDefault_CreatesLoadsAndOutputsSceneRef()
        {
            var path = ProbeFolder + "/Created.unity";
            var changes = new StepChangeRecorder(0, "create");
            var context = Context(
                new CreateSceneOperation().Descriptor,
                Inputs(("path", path)),
                SceneOut, changes);

            var result = new CreateSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var scene = SceneManager.GetSceneByPath(path);
            Assert.That(scene.isLoaded, Is.True);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(2));
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(path), Is.Not.Null,
                "the scene asset must exist at the path");
            Assert.That(scene.GetRootGameObjects().Select(r => r.name),
                Is.EqualTo(new[] { "Main Camera", "Directional Light" }),
                "the default setup produces the default roots");
            AssertSceneRef(context.Outputs["scene"], path);
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Created));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(path));
            Assert.That(changes.Records[0].Undoable, Is.False,
                "§I.1 declares Undo None");
        }

        [Test]
        public void Create_EmptySetup_CreatesSceneWithNoRoots()
        {
            var path = ProbeFolder + "/Empty.unity";

            var result = new CreateSceneOperation().Execute(Context(
                new CreateSceneOperation().Descriptor,
                Inputs(("path", path), ("setup", "empty"), ("mode", "single")),
                SceneOut));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var scene = SceneManager.GetSceneByPath(path);
            Assert.That(scene.isLoaded, Is.True);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(1), "single mode replaces");
            Assert.That(scene.GetRootGameObjects(), Is.Empty,
                "the empty setup produces no roots");
        }

        [Test]
        public void Create_ExistingPath_FailsOwUnity005()
        {
            var path = CreateSavedScene(ProbeFolder + "/Taken.unity");

            var result = new CreateSceneOperation().Execute(Context(
                new CreateSceneOperation().Descriptor,
                Inputs(("path", path)),
                SceneOut));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");

            var preflight = new CreateSceneOperation().Preflight(Context(
                new CreateSceneOperation().Descriptor,
                Inputs(("path", path)),
                SceneOut));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_005));
        }

        [Test]
        public void Create_SingleWithDirtyScenes_FailsOwUnity011()
        {
            EditorSceneManager.MarkSceneDirty(SceneManager.GetSceneByPath(BaseScenePath));

            var result = new CreateSceneOperation().Execute(Context(
                new CreateSceneOperation().Descriptor,
                Inputs(("path", ProbeFolder + "/Blocked.unity"), ("mode", "single")),
                SceneOut));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_011, null);
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(
                ProbeFolder + "/Blocked.unity"), Is.Null,
                "a refused create writes no file");
        }

        [Test]
        public void Create_NonUnityExtension_FailsOwUnity005()
        {
            var result = new CreateSceneOperation().Execute(Context(
                new CreateSceneOperation().Descriptor,
                Inputs(("path", ProbeFolder + "/NotAScene.txt")),
                SceneOut));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
        }

        // ── unity.scene.save ─────────────────────────────────────────────

        [Test]
        public void Save_ActiveScene_SavesAndOutputsSceneRef()
        {
            var baseScene = SceneManager.GetSceneByPath(BaseScenePath);
            EditorSceneManager.MarkSceneDirty(baseScene);
            Assert.That(baseScene.isDirty, Is.True, "the scene must be dirty before the save");
            var changes = new StepChangeRecorder(0, "save");
            var context = Context(
                new SaveSceneOperation().Descriptor,
                Inputs(),
                SceneOut, changes);

            var result = new SaveSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(SceneManager.GetSceneByPath(BaseScenePath).isDirty, Is.False,
                "a saved scene is no longer dirty");
            AssertSceneRef(context.Outputs["scene"], BaseScenePath);
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Saved));
            Assert.That(changes.Records[0].Undoable, Is.False,
                "§I.1 declares Undo None");
        }

        [Test]
        public void Save_ExplicitSceneToNewPath_SaveAs()
        {
            var baseScene = SceneManager.GetSceneByPath(BaseScenePath);
            var target = ProbeFolder + "/SavedAs.unity";
            var context = Context(
                new SaveSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(BaseScenePath)), ("path", target)),
                SceneOut);

            var result = new SaveSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(target), Is.Not.Null,
                "the Save-As target must hold the scene");
            AssertSceneRef(context.Outputs["scene"], target);
        }

        [Test]
        public void Save_UntitledWithoutPath_FailsOwUnity005()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            // The additive new scene is not made active by NewScene; the
            // untitled one must be activated for the omitted-scene path.
            SceneManager.SetActiveScene(
                SceneManager.GetSceneAt(SceneManager.sceneCount - 1));
            Assert.That(SceneManager.GetActiveScene().path, Is.Empty,
                "the fixture must leave an untitled active scene");

            var result = new SaveSceneOperation().Execute(Context(
                new SaveSceneOperation().Descriptor,
                Inputs(),
                SceneOut));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
        }

        [Test]
        public void Save_ToOccupiedOtherPath_FailsOwUnity005()
        {
            var other = CreateSavedScene(ProbeFolder + "/Other.unity");

            var result = new SaveSceneOperation().Execute(Context(
                new SaveSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(BaseScenePath)), ("path", other)),
                SceneOut));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
        }

        [Test]
        public void Save_NotLoadedScene_FailsOwUnity003()
        {
            var path = CreateSavedScene(ProbeFolder + "/Closed.unity");

            var result = new SaveSceneOperation().Execute(Context(
                new SaveSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(path))),
                SceneOut));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_003, "scene");
        }

        // ── unity.scene.close ────────────────────────────────────────────

        [Test]
        public void Close_LoadedScene_ClosesIt()
        {
            var other = CreateSavedScene(ProbeFolder + "/Other.unity");
            var context = Context(
                new CloseSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(other))),
                NoOutputs);

            var result = new CloseSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(SceneManager.GetSceneByPath(other).isLoaded, Is.False);
            Assert.That(SceneManager.sceneCount, Is.EqualTo(1));
        }

        [Test]
        public void Close_AlreadyClosed_Succeeds()
        {
            var path = CreateSavedScene(ProbeFolder + "/Closed.unity");
            var context = Context(
                new CloseSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(path))),
                NoOutputs);

            var result = new CloseSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                () => "closing an already closed scene is the §G5 item 2 success: " +
                    Describe(result));
        }

        [Test]
        public void Close_LastLoadedScene_FailsOwUnity011()
        {
            var result = new CloseSceneOperation().Execute(Context(
                new CloseSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(BaseScenePath))),
                NoOutputs));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_011, null);
            Assert.That(SceneManager.GetSceneByPath(BaseScenePath).isLoaded, Is.True,
                "the last scene stays loaded");
        }

        [Test]
        public void Close_DirtyScene_FailsOwUnity011()
        {
            var other = CreateSavedScene(ProbeFolder + "/Other.unity");
            var loaded = EditorSceneManager.OpenScene(other, OpenSceneMode.Additive);
            Assert.That(loaded.isLoaded, Is.True,
                "the fixture must load the dirty target");
            EditorSceneManager.MarkSceneDirty(loaded);

            var result = new CloseSceneOperation().Execute(Context(
                new CloseSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(other)), ("dirtyScenes", "fail")),
                NoOutputs));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_011, "dirtyScenes");
            Assert.That(SceneManager.GetSceneByPath(other).isLoaded, Is.True,
                "a refused close keeps the scene loaded");
        }

        [Test]
        public void Close_DirtyScene_Save_SavesThenCloses()
        {
            var other = CreateSavedScene(ProbeFolder + "/Other.unity");
            var loaded = EditorSceneManager.OpenScene(other, OpenSceneMode.Additive);
            Assert.That(loaded.isLoaded, Is.True,
                "the fixture must load the dirty target");
            EditorSceneManager.MarkSceneDirty(loaded);
            var changes = new StepChangeRecorder(0, "close");
            var context = Context(
                new CloseSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(other)), ("dirtyScenes", "save")),
                NoOutputs, changes);

            var result = new CloseSceneOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(SceneManager.GetSceneByPath(other).isLoaded, Is.False);
            Assert.That(changes.Records.Any(r => r.Action == ChangeAction.Saved), Is.True);
            Assert.That(changes.Records.All(r => !r.Undoable), Is.True,
                "§I.1 declares Undo None");
        }

        // ── pending edge-fed inputs (SPEC §D8 item 1) ────────────────────
        //
        // A Dry-Run pending input is counted present but holds no value:
        // Preflight must not read it — Get<T> on a pending port is the
        // programming-error InvalidOperationException — and takes the
        // port's declared default instead, the same answer a missing
        // optional port gives. The concrete-value controls beside them
        // prove the guards mask nothing the same paths already reported.

        [Test]
        public void Open_Preflight_PendingMode_TakesTheSingleDefault()
        {
            var path = CreateSavedScene(ProbeFolder + "/PendingMode.unity");
            EditorSceneManager.MarkSceneDirty(
                SceneManager.GetSceneByPath(BaseScenePath));
            var context = Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", path)),
                SceneOut,
                pending: new Dictionary<string, string>
                {
                    ["mode"] = "upstream-mode",
                });

            Assert.That(context.IsPending("mode"), Is.True);
            Assert.That(context.IsPresent("mode"), Is.True);

            var result = new OpenSceneOperation().Preflight(context);

            // The pending mode takes its "single" default, so the dirty
            // scene triggers the dirtyScenes="fail" refusal a missing mode
            // would — never a read of the pending placeholder.
            Assert.That(result.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_011));
            Assert.That(
                result.Diagnostics.First(
                    d => d.Code == ErrorCodes.OW_UNITY_011).Port,
                Is.EqualTo("dirtyScenes"));
        }

        [Test]
        public void Open_Preflight_PendingDirtyScenes_TakesTheFailDefault()
        {
            var path = CreateSavedScene(ProbeFolder + "/PendingDirty.unity");
            EditorSceneManager.MarkSceneDirty(
                SceneManager.GetSceneByPath(BaseScenePath));
            var context = Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", path), ("mode", "single")),
                SceneOut,
                pending: new Dictionary<string, string>
                {
                    ["dirtyScenes"] = "upstream-dirty",
                });

            Assert.That(context.IsPending("dirtyScenes"), Is.True);
            Assert.That(context.IsPresent("dirtyScenes"), Is.True);

            var result = new OpenSceneOperation().Preflight(context);

            Assert.That(result.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_011));
            Assert.That(
                result.Diagnostics.First(
                    d => d.Code == ErrorCodes.OW_UNITY_011).Port,
                Is.EqualTo("dirtyScenes"));
        }

        // The concrete-value control over the same fixture: a written
        // "additive" mode is read, so dirty scenes never enter the check.
        [Test]
        public void Open_Preflight_ConcreteAdditiveMode_ReportsNoRefusal()
        {
            var path = CreateSavedScene(ProbeFolder + "/Additive.unity");
            EditorSceneManager.MarkSceneDirty(
                SceneManager.GetSceneByPath(BaseScenePath));
            var context = Context(
                new OpenSceneOperation().Descriptor,
                Inputs(("path", path), ("mode", "additive")),
                SceneOut);

            var result = new OpenSceneOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Empty);
        }

        // And a written "save" is read: dirty scenes with paths pass the
        // single-mode check the pending default refused.
        [Test]
        public void Open_Preflight_ConcreteSaveDirtyScenes_ReportsNoRefusal()
        {
            var path = CreateSavedScene(ProbeFolder + "/SaveDirty.unity");
            EditorSceneManager.MarkSceneDirty(
                SceneManager.GetSceneByPath(BaseScenePath));
            var context = Context(
                new OpenSceneOperation().Descriptor,
                Inputs(
                    ("path", path), ("mode", "single"), ("dirtyScenes", "save")),
                SceneOut);

            var result = new OpenSceneOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Empty);
        }

        [Test]
        public void Close_Preflight_PendingDirtyScenes_TakesTheFailDefault()
        {
            var other = CreateSavedScene(ProbeFolder + "/ClosePending.unity");
            var loaded = EditorSceneManager.OpenScene(other, OpenSceneMode.Additive);
            Assert.That(loaded.isLoaded, Is.True,
                "the fixture must load the dirty target");
            EditorSceneManager.MarkSceneDirty(loaded);
            var context = Context(
                new CloseSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(other))),
                NoOutputs,
                pending: new Dictionary<string, string>
                {
                    ["dirtyScenes"] = "upstream-dirty",
                });

            Assert.That(context.IsPending("dirtyScenes"), Is.True);
            Assert.That(context.IsPresent("dirtyScenes"), Is.True);

            var result = new CloseSceneOperation().Preflight(context);

            Assert.That(result.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_011));
            Assert.That(
                result.Diagnostics.First(
                    d => d.Code == ErrorCodes.OW_UNITY_011).Port,
                Is.EqualTo("dirtyScenes"));
        }

        // The concrete-value control over the same fixture: a written
        // "save" is read, so the dirty target passes the check.
        [Test]
        public void Close_Preflight_ConcreteSaveDirtyScenes_ReportsNoRefusal()
        {
            var other = CreateSavedScene(ProbeFolder + "/CloseSave.unity");
            var loaded = EditorSceneManager.OpenScene(other, OpenSceneMode.Additive);
            Assert.That(loaded.isLoaded, Is.True,
                "the fixture must load the dirty target");
            EditorSceneManager.MarkSceneDirty(loaded);
            var context = Context(
                new CloseSceneOperation().Descriptor,
                Inputs(("scene", SceneRefJson(other)), ("dirtyScenes", "save")),
                NoOutputs);

            var result = new CloseSceneOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Empty);
        }

        [Test]
        public void Create_Preflight_PendingMode_TakesTheAdditiveDefault()
        {
            var context = Context(
                new CreateSceneOperation().Descriptor,
                Inputs(("path", ProbeFolder + "/PendingMode.unity")),
                SceneOut,
                pending: new Dictionary<string, string>
                {
                    ["mode"] = "upstream-mode",
                });

            Assert.That(context.IsPending("mode"), Is.True);
            Assert.That(context.IsPresent("mode"), Is.True);

            var result = new CreateSceneOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(
                (string?)result.Snapshot.ToToken()["mode"],
                Is.EqualTo("additive"),
                "the pending mode is recorded as its declared default");
        }

        [Test]
        public void Create_Preflight_PendingSetup_TakesTheDefaultSetup()
        {
            var context = Context(
                new CreateSceneOperation().Descriptor,
                Inputs(("path", ProbeFolder + "/PendingSetup.unity")),
                SceneOut,
                pending: new Dictionary<string, string>
                {
                    ["setup"] = "upstream-setup",
                });

            Assert.That(context.IsPending("setup"), Is.True);
            Assert.That(context.IsPresent("setup"), Is.True);

            var result = new CreateSceneOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(
                (string?)result.Snapshot.ToToken()["setup"],
                Is.EqualTo("default"),
                "the pending setup is recorded as its declared default");
        }

        // The concrete-value control over the same fixture: a written
        // "single" mode is read, so the dirty scene refuses the create the
        // pending additive default would allow.
        [Test]
        public void Create_Preflight_ConcreteSingleMode_ReportsOwUnity011()
        {
            EditorSceneManager.MarkSceneDirty(
                SceneManager.GetSceneByPath(BaseScenePath));
            var context = Context(
                new CreateSceneOperation().Descriptor,
                Inputs(
                    ("path", ProbeFolder + "/ConcreteSingle.unity"),
                    ("mode", "single")),
                SceneOut);

            var result = new CreateSceneOperation().Preflight(context);

            Assert.That(result.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_011));
        }

        // And a written "empty" setup is read into the snapshot, where the
        // pending default recorded "default" and its expected roots.
        [Test]
        public void Create_Preflight_ConcreteEmptySetup_RecordsEmpty()
        {
            var context = Context(
                new CreateSceneOperation().Descriptor,
                Inputs(
                    ("path", ProbeFolder + "/ConcreteEmpty.unity"),
                    ("setup", "empty")),
                SceneOut);

            var result = new CreateSceneOperation().Preflight(context);

            Assert.That(result.Diagnostics, Is.Empty);
            var snapshot = result.Snapshot.ToToken();
            Assert.That((string?)snapshot["setup"], Is.EqualTo("empty"));
            Assert.That((JArray)snapshot["expectedRoots"]!, Is.Empty,
                "the empty setup expects no roots");
        }

        // ── fixtures ─────────────────────────────────────────────────────

        // Creates and saves a new scene additively at the path, then closes
        // it, leaving a scene asset on disk and out of the loaded set.
        private static string CreateSavedScene(string path)
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True,
                "fixture scene must save: " + path);
            Assert.That(EditorSceneManager.CloseScene(scene, true), Is.True,
                "fixture scene must close: " + path);
            return path;
        }

        private static Dictionary<string, InputValue> Inputs(
            params (string Port, string Value)[] values)
        {
            var inputs = new Dictionary<string, InputValue>();
            foreach (var (port, value) in values)
            {
                inputs[port] = InputValue.Present(
                    value.StartsWith("{", StringComparison.Ordinal)
                        ? Json(value)
                        : Json("\"" + value + "\""));
            }
            return inputs;
        }

        private static string SceneRefJson(string path) =>
            "{\"kind\":\"scene\",\"path\":\"" + path + "\",\"guid\":\"" +
            AssetDatabase.AssetPathToGUID(path) + "\"}";

        private static UnityStepContext Context(
            OperationDescriptor descriptor,
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string> outputTypes,
            IChangeRecorder? changes = null,
            IReadOnlyDictionary<string, string>? pending = null)
        {
            var inputTypes = new Dictionary<string, string>();
            foreach (var port in descriptor.Inputs)
            {
                inputTypes[port.Name] = port.Type;
            }
            return new UnityStepContext(
                inputs,
                inputTypes,
                outputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                changes ?? new StepChangeRecorder(0, "scene"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                pendingProducers: pending);
        }

        private static void AssertSceneRef(JsonValue output, string path)
        {
            var token = output.ToToken();
            Assert.That((string?)token["kind"], Is.EqualTo("scene"));
            Assert.That((string?)token["path"], Is.EqualTo(path));
            Assert.That((string?)token["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));
        }

        private static void AssertFailedWith(StepResult result, string code, string? port)
        {
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed), () => Describe(result));
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo(code), () => Describe(result));
            if (port is not null)
            {
                Assert.That(result.Error!.Port, Is.EqualTo(port));
            }
        }

        private static string Describe(StepResult result) =>
            result.Kind +
            (result.Error is null
                ? string.Empty
                : " " + result.Error.Code + " " + result.Error.Message);

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
