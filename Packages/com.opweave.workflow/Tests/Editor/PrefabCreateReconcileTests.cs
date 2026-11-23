#nullable enable

// T-W2.6-02 [Edit] tests (local:T-W2.6-02:prefab-create-reconcile): the six
// SPEC §F5 Operation-level cases for the three Prefab-creating Operations —
// not executed; executed but StepSucceeded not recorded; partial side
// effect; pre-existing same-name artifact; artifact modified by another
// operation; simulated Editor restart (different editorSessionId) — plus
// [UnityTest] cases crossing a real domain reload (RecompileScripts,
// SPEC §T.3). Reconcile returns Completed only with the full evidence row,
// NotApplied only when proven, otherwise Unknown; no case duplicates or
// mis-deletes an artifact.
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
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>§F5 reconciliation of the three Prefab-creating Operations.</summary>
    internal sealed class PrefabCreateReconcileTests
    {
        private const string Folder = "Assets/OpWeavePrefabContentsProbe";
        private const string TargetPath = Folder + "/Created.prefab";
        private const string BasePath = Folder + "/Base.prefab";
        private const string VariantPath = Folder + "/Variant.prefab";

        private static readonly IReadOnlyDictionary<string, string> CreateIn =
            new Dictionary<string, string>
            {
                ["source"] = "unity.GameObjectRef",
                ["path"] = "unity.AssetPath",
                ["connect"] = "core.Bool",
                ["overwrite"] = "core.Bool",
            };
        private static readonly IReadOnlyDictionary<string, string> CreateOut =
            new Dictionary<string, string>
            {
                ["prefab"] = "unity.PrefabAssetRef",
                ["instance"] = "unity.PrefabInstanceRef",
            };
        private static readonly IReadOnlyDictionary<string, string> InstIn =
            new Dictionary<string, string>
            {
                ["prefab"] = "unity.PrefabAssetRef",
                ["scene"] = "unity.SceneRef",
                ["parent"] = "unity.GameObjectRef",
                ["position"] = "unity.Vector3",
            };
        private static readonly IReadOnlyDictionary<string, string> InstOut =
            new Dictionary<string, string> { ["instance"] = "unity.PrefabInstanceRef" };
        private static readonly IReadOnlyDictionary<string, string> VarIn =
            new Dictionary<string, string>
            {
                ["base"] = "unity.PrefabAssetRef",
                ["path"] = "unity.AssetPath",
            };
        private static readonly IReadOnlyDictionary<string, string> VarOut =
            new Dictionary<string, string> { ["variant"] = "unity.PrefabAssetRef" };

        private readonly List<GameObject> _scratch = new List<GameObject>();

        // The plain [SetUp] does no scene reset: UTF re-runs it on the
        // domain-reload resume inside the [UnityTest]s, and a Single-mode
        // NewScene there would close the scene the reload is meant to
        // leave untouched (the SceneCreateReconcileTests hazard). The
        // [TearDown] runs once per test, so the reset lives there.
        [SetUp]
        public void SetUp() => PrefabContentsFixture.EnsureFolder(
            Folder, "OpWeavePrefabContentsProbe");

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _scratch)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
            _scratch.Clear();
            PrefabContentsFixture.Cleanup();
            // Instantiating, connecting, reparenting and renaming in the
            // active scene marks it dirty; a dirty scene makes the next
            // harness fixture refuse to open its scene, so the test hands
            // over the same clean untitled scene it found. The objects the
            // scene still holds — a post-reload fixture never re-collects
            // its _scratch — are discarded with it.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private GameObject Scratch(string name)
        {
            var go = new GameObject(name);
            _scratch.Add(go);
            return go;
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string> inputTypes,
            IReadOnlyDictionary<string, string> outputTypes) =>
            new UnityStepContext(
                inputs, inputTypes, outputTypes, RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "prefab-create"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

        private static InputValue Present(string json) =>
            InputValue.Present(JsonValue.Parse(json).Value!);

        private static JsonValue PrefabRef(string path) =>
            PrefabContentsFixture.PrefabRef(
                AssetDatabase.AssetPathToGUID(path), path);

        private static Dictionary<string, InputValue> CreateInputs(
            GameObject source, bool? overwrite = null)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["source"] = InputValue.Present(
                    new RefResolver().ToRef(source, "unity.GameObjectRef")),
                ["path"] = Present("\"" + TargetPath + "\""),
            };
            if (overwrite is { } o)
            {
                inputs["overwrite"] = Present(o ? "true" : "false");
            }
            return inputs;
        }

        private static Dictionary<string, InputValue> InstInputs(
            GameObject? parent = null)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["prefab"] = InputValue.Present(PrefabRef(BasePath)),
            };
            if (parent is not null)
            {
                inputs["parent"] = InputValue.Present(
                    new RefResolver().ToRef(parent, "unity.GameObjectRef"));
            }
            return inputs;
        }

        private static Dictionary<string, InputValue> VarInputs() =>
            new Dictionary<string, InputValue>
            {
                ["base"] = InputValue.Present(PrefabRef(BasePath)),
                ["path"] = Present("\"" + VariantPath + "\""),
            };

        private static void AssertUnknown(ReconcileResult verdict, string fragment)
        {
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => verdict is ReconcileResult.Unknown u ? u.Reason : verdict.GetType().Name);
            Assert.That(((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain(fragment));
        }

        private static JsonValue Restarted(JsonValue snapshot)
        {
            var restarted = (JObject)snapshot.ToToken().DeepClone();
            restarted["editorSessionId"] = "restarted-session";
            return JsonValue.From(restarted);
        }

        private void AdoptInstanceOutputs(UnityStepContext context)
        {
            if (context.Outputs.TryGetValue("instance", out var instance))
            {
                var resolved = new RefResolver().Resolve<GameObject>(instance);
                if (resolved.Ok && resolved.Value is not null)
                {
                    _scratch.Add(resolved.Value);
                }
            }
        }

        // ══ unity.prefab.create_from_gameobject ══════════════════════════

        // Case 1 — interrupted before Execute.
        [Test]
        public void Create_NotExecuted_ReturnsNotApplied()
        {
            var source = Scratch("Src");
            var snapshot = new PrefabCreateFromGameObjectOperation().Preflight(
                Context(CreateInputs(source), CreateIn, CreateOut)).Snapshot;

            var verdict = new PrefabCreateFromGameObjectOperation().Reconcile(
                Context(CreateInputs(source), CreateIn, CreateOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 2 — executed, StepSucceeded not recorded: the asset exists
        // with the recorded fingerprint and the source is connected.
        [Test]
        public void Create_ExecutedUnrecorded_ReturnsCompletedWithOutputs()
        {
            var source = Scratch("Src");
            var op = new PrefabCreateFromGameObjectOperation();
            var inputs = CreateInputs(source);
            var snapshot = op.Preflight(Context(inputs, CreateIn, CreateOut)).Snapshot;
            var context = Context(inputs, CreateIn, CreateOut);
            Assert.That(op.Execute(context).Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = op.Reconcile(Context(inputs, CreateIn, CreateOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.That(
                (string?)((JObject)outputs["prefab"].ToToken())["path"],
                Is.EqualTo(TargetPath));
            Assert.That(outputs.ContainsKey("instance"), Is.True);
            Assert.That(
                JToken.DeepEquals(
                    (JObject)outputs["instance"].ToToken(),
                    (JObject)context.Outputs["instance"].ToToken()),
                Is.True, "restored outputs equal the uninterrupted run's");
            _scratch.Remove(source);
        }

        // Case 3 — partial side effect: the asset exists but the source was
        // never connected (connect evidence cannot be proven).
        [Test]
        public void Create_PartialAssetWithoutConnect_ReturnsUnknown()
        {
            var source = Scratch("Src");
            var op = new PrefabCreateFromGameObjectOperation();
            var inputs = CreateInputs(source);
            var snapshot = op.Preflight(Context(inputs, CreateIn, CreateOut)).Snapshot;
            // Foreign apply: save without connecting — the fingerprint
            // matches but the connect clause does not.
            Assert.That(PrefabUtility.SaveAsPrefabAsset(source, TargetPath),
                Is.Not.Null);

            var verdict = op.Reconcile(Context(inputs, CreateIn, CreateOut), snapshot);

            AssertUnknown(verdict, "outermost instance");
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath),
                Is.Not.Null, "an Unknown verdict never deletes the artifact");
        }

        // Case 4 — a pre-existing same-name artifact: the snapshot recorded
        // it, so nothing at the path can be attributed to the step.
        [Test]
        public void Create_PreexistingArtifact_NeverCompleted()
        {
            var source = Scratch("Src");
            // Crafted snapshot: the row's own member names, overwrite=false
            // with a recorded pre-existing artifact — a state an honest
            // Preflight (which refuses the occupied path) never emits.
            var crafted = new JObject
            {
                ["path"] = TargetPath,
                ["pathExisted"] = true,
                ["connect"] = true,
                ["overwrite"] = false,
                ["before"] = new JObject { ["guid"] = "ffffffffffffffffffffffffffffffff" },
                ["expected"] = new JObject(),
                ["source"] = GameObjectEvidence.RefOf(source),
                ["sourcePrefabState"] = PrefabCreateEvidence.SourcePrefabState(source),
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            PrefabContentsFixture.CreateProbePrefab("Created.prefab");

            var verdict = new PrefabCreateFromGameObjectOperation().Reconcile(
                Context(CreateInputs(source), CreateIn, CreateOut),
                JsonValue.From(crafted));

            Assert.That(verdict, Is.Not.TypeOf<ReconcileResult.Completed>());
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath),
                Is.Not.Null, "the pre-existing artifact is never deleted");
        }

        // Case 5 — the artifact was changed by another operation after the
        // step ran: the content no longer matches the fingerprint.
        [Test]
        public void Create_ModifiedArtifact_ReturnsUnknown_AndChangesNothing()
        {
            var source = Scratch("Src");
            var op = new PrefabCreateFromGameObjectOperation();
            var inputs = CreateInputs(source);
            var snapshot = op.Preflight(Context(inputs, CreateIn, CreateOut)).Snapshot;
            Assert.That(op.Execute(Context(inputs, CreateIn, CreateOut)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var foreign = Scratch("Foreign");
            // The overwrite must differ in the fingerprint's terms: a bare
            // foreign root normalizes to the same file-stem name as the
            // recorded expectation and hashes identically, so the foreign
            // artifact carries a child the snapshot never expected.
            new GameObject("ForeignChild").transform.SetParent(
                foreign.transform, false);
            Assert.That(PrefabUtility.SaveAsPrefabAsset(foreign, TargetPath),
                Is.Not.Null, "the fixture must overwrite the artifact");

            var verdict = op.Reconcile(Context(inputs, CreateIn, CreateOut), snapshot);

            AssertUnknown(verdict, "differs");
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(TargetPath),
                Is.Not.Null, "a modified artifact is left untouched");
        }

        // Case 6 — simulated Editor restart: connect evidence needs the
        // same session, so the connect run goes Unknown while a
        // connect=false artifact stays re-identifiable by its file keys.
        [Test]
        public void Create_SimulatedRestart_FollowsTheConnectClause()
        {
            var source = Scratch("Src");
            var op = new PrefabCreateFromGameObjectOperation();
            var inputs = CreateInputs(source);
            var snapshot = op.Preflight(Context(inputs, CreateIn, CreateOut)).Snapshot;
            Assert.That(op.Execute(Context(inputs, CreateIn, CreateOut)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            AssertUnknown(
                op.Reconcile(Context(inputs, CreateIn, CreateOut), Restarted(snapshot)),
                "session");

            // connect=false: the file artifact re-identifies across the
            // session boundary — Completed still follows the evidence.
            var source2 = Scratch("Src2");
            var inputs2 = new Dictionary<string, InputValue>(CreateInputs(source2))
            {
                ["connect"] = Present("false"),
            };
            var path2 = Folder + "/Second.prefab";
            inputs2["path"] = Present("\"" + path2 + "\"");
            var snapshot2b = op.Preflight(Context(inputs2, CreateIn, CreateOut)).Snapshot;
            Assert.That(op.Execute(Context(inputs2, CreateIn, CreateOut)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict2 = op.Reconcile(
                Context(inputs2, CreateIn, CreateOut), Restarted(snapshot2b));

            Assert.That(verdict2, Is.TypeOf<ReconcileResult.Completed>(),
                "a file artifact re-identifies across a session boundary");
            Assert.That(AssetDatabase.DeleteAsset(path2), Is.True);
        }

        // ══ unity.prefab.instantiate ═════════════════════════════════════

        [Test]
        public void Instantiate_NotExecuted_ReturnsNotApplied()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var parent = Scratch("Parent");
            var snapshot = new PrefabInstantiateOperation().Preflight(
                Context(InstInputs(parent), InstIn, InstOut)).Snapshot;

            var verdict = new PrefabInstantiateOperation().Reconcile(
                Context(InstInputs(parent), InstIn, InstOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        [Test]
        public void Instantiate_ExecutedUnrecorded_ReturnsCompletedWithInstance()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var parent = Scratch("Parent");
            var op = new PrefabInstantiateOperation();
            var inputs = InstInputs(parent);
            var snapshot = op.Preflight(Context(inputs, InstIn, InstOut)).Snapshot;
            var context = Context(inputs, InstIn, InstOut);
            Assert.That(op.Execute(context).Kind, Is.EqualTo(StepResultKind.Succeeded));
            AdoptInstanceOutputs(context);

            var verdict = op.Reconcile(Context(inputs, InstIn, InstOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.That(
                JToken.DeepEquals(
                    (JObject)outputs["instance"].ToToken(),
                    (JObject)context.Outputs["instance"].ToToken()),
                Is.True, "restored outputs equal the uninterrupted run's");
        }

        // Case 3 — partial side effect: the one new child is not a Prefab
        // instance at all — not attributable, not safely re-runnable.
        [Test]
        public void Instantiate_PartialNonInstanceChild_ReturnsUnknown()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var parent = Scratch("Parent");
            var snapshot = new PrefabInstantiateOperation().Preflight(
                Context(InstInputs(parent), InstIn, InstOut)).Snapshot;
            var foreign = new GameObject("SomeoneElsesChild");
            _scratch.Add(foreign);
            foreign.transform.SetParent(parent.transform, false);

            var verdict = new PrefabInstantiateOperation().Reconcile(
                Context(InstInputs(parent), InstIn, InstOut), snapshot);

            AssertUnknown(verdict, "instance");
            Assert.That(foreign, Is.Not.Null, "nothing is deleted");
        }

        // Case 4 — a pre-existing instance recorded in the snapshot: an
        // unchanged child list is provably NotApplied, never Completed.
        [Test]
        public void Instantiate_PreexistingInstance_NotCompleted()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var parent = Scratch("Parent");
            var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BasePath);
            var preexisting = (GameObject)PrefabUtility.InstantiatePrefab(
                baseAsset, parent.scene);
            _scratch.Add(preexisting);
            preexisting.transform.SetParent(parent.transform, false);
            var snapshot = new PrefabInstantiateOperation().Preflight(
                Context(InstInputs(parent), InstIn, InstOut)).Snapshot;

            var verdict = new PrefabInstantiateOperation().Reconcile(
                Context(InstInputs(parent), InstIn, InstOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 5 — the artifact was renamed after the step ran: an
        // override beyond the placement — Unknown, never Completed.
        [Test]
        public void Instantiate_ModifiedArtifact_ReturnsUnknown()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var parent = Scratch("Parent");
            var op = new PrefabInstantiateOperation();
            var inputs = InstInputs(parent);
            var snapshot = op.Preflight(Context(inputs, InstIn, InstOut)).Snapshot;
            var context = Context(inputs, InstIn, InstOut);
            Assert.That(op.Execute(context).Kind, Is.EqualTo(StepResultKind.Succeeded));
            AdoptInstanceOutputs(context);
            var resolved = new RefResolver().Resolve<GameObject>(
                context.Outputs["instance"]);
            resolved.Value!.name = "Renamed";

            var verdict = op.Reconcile(Context(inputs, InstIn, InstOut), snapshot);

            AssertUnknown(verdict, "override");
        }

        // Case 6 — simulated Editor restart: the row's verdicts are
        // same-session; a different session is Unknown.
        [Test]
        public void Instantiate_SimulatedRestart_ReturnsUnknown()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var parent = Scratch("Parent");
            var op = new PrefabInstantiateOperation();
            var inputs = InstInputs(parent);
            var snapshot = op.Preflight(Context(inputs, InstIn, InstOut)).Snapshot;
            var context = Context(inputs, InstIn, InstOut);
            Assert.That(op.Execute(context).Kind, Is.EqualTo(StepResultKind.Succeeded));
            AdoptInstanceOutputs(context);

            var verdict = op.Reconcile(
                Context(inputs, InstIn, InstOut), Restarted(snapshot));

            AssertUnknown(verdict, "session");
        }

        // ══ unity.prefab.create_variant ══════════════════════════════════

        [Test]
        public void Variant_NotExecuted_ReturnsNotApplied()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var snapshot = new PrefabCreateVariantOperation().Preflight(
                Context(VarInputs(), VarIn, VarOut)).Snapshot;

            var verdict = new PrefabCreateVariantOperation().Reconcile(
                Context(VarInputs(), VarIn, VarOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        [Test]
        public void Variant_ExecutedUnrecorded_ReturnsCompletedWithVariant()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var op = new PrefabCreateVariantOperation();
            var inputs = VarInputs();
            var snapshot = op.Preflight(Context(inputs, VarIn, VarOut)).Snapshot;
            var context = Context(inputs, VarIn, VarOut);
            Assert.That(op.Execute(context).Kind, Is.EqualTo(StepResultKind.Succeeded));

            var verdict = op.Reconcile(Context(inputs, VarIn, VarOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.That(
                (string?)((JObject)outputs["variant"].ToToken())["path"],
                Is.EqualTo(VariantPath));
        }

        // Case 3 — partial side effect: a plain (non-Variant) Prefab
        // occupies the path — not attributable, never re-run over.
        [Test]
        public void Variant_PartialNonVariantArtifact_ReturnsUnknown()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var op = new PrefabCreateVariantOperation();
            var inputs = VarInputs();
            var snapshot = op.Preflight(Context(inputs, VarIn, VarOut)).Snapshot;
            var foreign = Scratch("Foreign");
            Assert.That(PrefabUtility.SaveAsPrefabAsset(foreign, VariantPath),
                Is.Not.Null);

            var verdict = op.Reconcile(Context(inputs, VarIn, VarOut), snapshot);

            AssertUnknown(verdict, "Variant");
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath),
                Is.Not.Null, "an Unknown verdict never deletes the artifact");
        }

        // Case 4 — a pre-existing same-name artifact: the snapshot
        // recorded it, so nothing at the path can be attributed to the
        // step. An honest Preflight refuses the occupied path, so the
        // recorded pre-existing state is crafted by flipping the
        // empty-path row's pathExisted flag (the SceneCreate and
        // ScriptTemplate case-4 pattern).
        [Test]
        public void Variant_PreexistingArtifact_NeverCompleted()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var op = new PrefabCreateVariantOperation();
            var inputs = VarInputs();
            var snapshot = (JObject)op.Preflight(
                Context(inputs, VarIn, VarOut)).Snapshot.ToToken();
            PrefabContentsFixture.CreateProbePrefab("Variant.prefab");
            snapshot["pathExisted"] = true;

            var verdict = op.Reconcile(
                Context(inputs, VarIn, VarOut), JsonValue.From(snapshot));

            AssertUnknown(verdict, "pre-existing");
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath),
                Is.Not.Null, "the pre-existing artifact is never deleted");
        }

        // A post-snapshot foreign artifact — a Variant of a different
        // base created after the empty-path snapshot — is never this
        // step's product either.
        [Test]
        public void Variant_ForeignVariantOfOtherBase_ReturnsUnknown()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            PrefabContentsFixture.CreateProbePrefab("Other.prefab");
            var op = new PrefabCreateVariantOperation();
            var inputs = VarInputs();
            var snapshot = op.Preflight(Context(inputs, VarIn, VarOut)).Snapshot;
            var other = AssetDatabase.LoadAssetAtPath<GameObject>(
                Folder + "/Other.prefab");
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(other, preview);
                Assert.That(PrefabUtility.SaveAsPrefabAsset(instance, VariantPath),
                    Is.Not.Null);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }

            var verdict = op.Reconcile(Context(inputs, VarIn, VarOut), snapshot);

            Assert.That(verdict, Is.Not.TypeOf<ReconcileResult.Completed>());
        }

        // Case 5 — the artifact was re-saved with different content after
        // the step ran: the fingerprint no longer matches.
        [Test]
        public void Variant_ModifiedArtifact_ReturnsUnknown()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var op = new PrefabCreateVariantOperation();
            var inputs = VarInputs();
            var snapshot = op.Preflight(Context(inputs, VarIn, VarOut)).Snapshot;
            Assert.That(op.Execute(Context(inputs, VarIn, VarOut)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var foreign = Scratch("ForeignModified");
            Assert.That(PrefabUtility.SaveAsPrefabAsset(foreign, VariantPath),
                Is.Not.Null, "the fixture must overwrite the variant");

            var verdict = op.Reconcile(Context(inputs, VarIn, VarOut), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>());
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath),
                Is.Not.Null, "a modified artifact is left untouched");
        }

        // Case 6 — simulated Editor restart: the Variant is a file
        // artifact re-identifiable by path/guid/content keys, so the
        // verdict still follows the evidence (§F5 common rule 3).
        [Test]
        public void Variant_SimulatedRestart_StillDecidesByEvidence()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var op = new PrefabCreateVariantOperation();
            var inputs = VarInputs();
            var snapshot = op.Preflight(Context(inputs, VarIn, VarOut)).Snapshot;
            Assert.That(op.Execute(Context(inputs, VarIn, VarOut)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict = op.Reconcile(
                Context(inputs, VarIn, VarOut), Restarted(snapshot));

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "a file artifact is re-identifiable across a session boundary");
        }

        // ══ [UnityTest] real domain reload (SPEC §T.3) ═══════════════════

        private static object? s_sentinel;
        private static string ProbeDir =>
            Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "OpWeavePrefabCreateProbe");
        private static string SnapshotPath(string name) =>
            Path.Combine(ProbeDir, name + ".json");
        private static string MarkerDir =>
            Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Assets", "OpWeavePrefabCreateReload");

        private static void PersistSnapshot(string name, JsonValue snapshot)
        {
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(SnapshotPath(name), snapshot.CanonicalText);
        }

        private static JsonValue ReadSnapshot(string name)
        {
            var parsed = JsonValue.Parse(File.ReadAllText(SnapshotPath(name)));
            Assert.That(parsed.Ok && parsed.Value is not null, Is.True);
            return parsed.Value!;
        }

        private static void ArmReload()
        {
            Directory.CreateDirectory(MarkerDir);
            // The marker source must differ on every arm: a byte-identical
            // file left behind by an earlier failed run re-imports to the
            // same assembly, so no recompile — and no domain reload —
            // would follow. A fresh guid comment guarantees a source
            // change Unity must compile.
            File.WriteAllText(
                Path.Combine(MarkerDir, "PrefabCreateReloadMarker.cs"),
                "// " + System.Guid.NewGuid().ToString("N") + "\n" +
                "internal static class PrefabCreateReloadMarker { }\n");
            AssetDatabase.Refresh();
            s_sentinel = new object();
        }

        // Executes one operation, persists its snapshot under Library/ and
        // arms the marker compile that forces the domain reload.
        private string PhaseBeforeReload(string which)
        {
            try
            {
                PrefabContentsFixture.CreateProbePrefab("Base.prefab");
                var parent = new GameObject("ReloadParent");
                _scratch.Add(parent);
                switch (which)
                {
                    case "instantiate":
                    {
                        var op = new PrefabInstantiateOperation();
                        var inputs = InstInputs(parent);
                        var snapshot = op.Preflight(
                            Context(inputs, InstIn, InstOut)).Snapshot;
                        var context = Context(inputs, InstIn, InstOut);
                        if (op.Execute(context).Kind != StepResultKind.Succeeded)
                        {
                            return "instantiate execute failed";
                        }
                        PersistSnapshot(which, snapshot);
                        break;
                    }
                    case "variant":
                    {
                        var op = new PrefabCreateVariantOperation();
                        var inputs = VarInputs();
                        var snapshot = op.Preflight(
                            Context(inputs, VarIn, VarOut)).Snapshot;
                        if (op.Execute(Context(inputs, VarIn, VarOut)).Kind !=
                            StepResultKind.Succeeded)
                        {
                            return "variant execute failed";
                        }
                        PersistSnapshot(which, snapshot);
                        break;
                    }
                    default:
                    {
                        var op = new PrefabCreateFromGameObjectOperation();
                        var source = new GameObject("ReloadSrc");
                        _scratch.Add(source);
                        var inputs = CreateInputs(source);
                        var snapshot = op.Preflight(
                            Context(inputs, CreateIn, CreateOut)).Snapshot;
                        if (op.Execute(Context(inputs, CreateIn, CreateOut)).Kind !=
                            StepResultKind.Succeeded)
                        {
                            return "create execute failed";
                        }
                        PersistSnapshot(which, snapshot);
                        break;
                    }
                }
                ArmReload();
                return string.Empty;
            }
            catch (System.Exception e)
            {
                return "phase-1 threw: " + e.Message;
            }
        }

        // After the reload: nothing is carried in memory — the persisted
        // snapshot is re-read and Reconcile must still reach Completed.
        private string PhaseAfterReload(string which)
        {
            try
            {
                var snapshot = ReadSnapshot(which);
                ReconcileResult verdict;
                switch (which)
                {
                    case "instantiate":
                    {
                        var parent = GameObject.Find("ReloadParent");
                        if (parent == null)
                        {
                            return "the reload parent is gone";
                        }
                        verdict = new PrefabInstantiateOperation().Reconcile(
                            Context(InstInputs(parent), InstIn, InstOut), snapshot);
                        break;
                    }
                    case "variant":
                        verdict = new PrefabCreateVariantOperation().Reconcile(
                            Context(VarInputs(), VarIn, VarOut), snapshot);
                        break;
                    default:
                    {
                        var source = GameObject.Find("ReloadSrc");
                        if (source == null)
                        {
                            return "the reload source is gone";
                        }
                        verdict = new PrefabCreateFromGameObjectOperation().Reconcile(
                            Context(CreateInputs(source), CreateIn, CreateOut),
                            snapshot);
                        break;
                    }
                }
                return verdict is ReconcileResult.Completed
                    ? string.Empty
                    : "expected Completed, got " +
                        (verdict is ReconcileResult.Unknown unknown
                            ? "Unknown(" + unknown.Reason + ")"
                            : verdict.GetType().Name);
            }
            catch (System.Exception e)
            {
                return "phase-2 threw: " + e.Message;
            }
        }

        private IEnumerator ReloadRoundTrip(string which)
        {
            var before = PhaseBeforeReload(which);
            Assert.That(before, Is.Empty, "pre-reload phase failed");
            yield return new RecompileScripts();
            var guard = 0;
            while (s_sentinel is not null && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsNull(s_sentinel, "the marker compile did not produce a domain reload");
            var after = PhaseAfterReload(which);
            Assert.That(after, Is.Empty, "post-reload phase failed");
        }

        // A failed phase must never poison the next test: the UnitySetUp /
        // UnityTearDown pair sweeps the persisted snapshot, the marker
        // script and the probe assets whatever the test asserted (the
        // GameObjectReconcileTests pattern), then awaits the recompile the
        // marker removal schedules. Returns true when a script asset went
        // away and the domain will reload.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            if (CleanupReloadArtifacts())
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (CleanupReloadArtifacts())
            {
                yield return new RecompileScripts(false);
            }
        }

        private static bool CleanupReloadArtifacts()
        {
            var dirty = false;
            foreach (var path in new[] { TargetPath, VariantPath, BasePath })
            {
                if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
            }
            s_sentinel = null;
            // The pre-reload phase tracks its scene objects in _scratch,
            // but the reload recreates this fixture with an empty list —
            // the survivors are re-found by name and destroyed here.
            foreach (var strayName in new[] { "ReloadParent", "ReloadSrc" })
            {
                var stray = GameObject.Find(strayName);
                if (stray != null)
                {
                    Object.DestroyImmediate(stray);
                }
            }
            var markerAsset = "Assets/OpWeavePrefabCreateReload";
            if (AssetDatabase.IsValidFolder(markerAsset))
            {
                AssetDatabase.DeleteAsset(markerAsset);
                dirty = true;
            }
            if (Directory.Exists(MarkerDir))
            {
                try { Directory.Delete(MarkerDir, true); dirty = true; }
                catch (System.Exception) { }
            }
            if (File.Exists(MarkerDir + ".meta"))
            {
                try { File.Delete(MarkerDir + ".meta"); dirty = true; }
                catch (System.Exception) { }
            }
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, true); } catch (System.Exception) { }
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            return dirty;
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Instantiate_Reconcile_AfterRealDomainReload_Completed() =>
            ReloadRoundTrip("instantiate");

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Variant_Reconcile_AfterRealDomainReload_Completed() =>
            ReloadRoundTrip("variant");

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Create_Reconcile_AfterRealDomainReload_Completed() =>
            ReloadRoundTrip("create");
    }
}
