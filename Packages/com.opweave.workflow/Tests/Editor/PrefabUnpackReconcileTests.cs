#nullable enable

// T-W2.6-04 [Edit] tests (local:T-W2.6-04:prefab-unpack-reconcile): the
// six SPEC §F5 Operation-level cases for unity.prefab.unpack — not
// executed; executed but StepSucceeded not recorded; partial side
// effect; pre-existing same-name artifact; artifact modified by another
// operation; simulated Editor restart (different editorSessionId) —
// plus a [UnityTest] case crossing a real domain reload
// (RecompileScripts, SPEC §T.3). Reconcile returns Completed only with
// the full evidence row — same session, same objects, same structure,
// the mode's expected membership state — NotApplied only when proven,
// otherwise Unknown; no case duplicates or mis-deletes an artifact.
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>§F5 reconciliation of <c>unity.prefab.unpack</c>.</summary>
    internal sealed class PrefabUnpackReconcileTests
    {
        private const string BasePath =
            "Assets/OpWeavePrefabContentsProbe/Base.prefab";
        private const string NestedPath =
            "Assets/OpWeavePrefabContentsProbe/NestedBase.prefab";
        private const string ContainerPath =
            "Assets/OpWeavePrefabContentsProbe/Container.prefab";
        private const string TwoChildPath =
            "Assets/OpWeavePrefabContentsProbe/TwoChildren.prefab";

        private static readonly IReadOnlyDictionary<string, string> UnpackIn =
            new Dictionary<string, string>
            {
                ["instance"] = "unity.PrefabInstanceRef",
                ["mode"] = "core.String",
            };
        private static readonly IReadOnlyDictionary<string, string> UnpackOut =
            new Dictionary<string, string>
            {
                ["gameObject"] = "unity.GameObjectRef",
            };

        private readonly List<GameObject> _scratch = new List<GameObject>();

        // The plain [SetUp] does no scene reset: UTF re-runs it on the
        // domain-reload resume inside the [UnityTest], and a Single-mode
        // NewScene there would close the scene the reload is meant to
        // leave untouched (the PrefabCreateReconcileTests pattern). The
        // [TearDown] runs once per test, so the reset lives there.
        [SetUp]
        public void SetUp() => PrefabContentsFixture.EnsureFolder(
            "Assets/OpWeavePrefabContentsProbe",
            "OpWeavePrefabContentsProbe");

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
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private GameObject Instantiate(string assetPath, string? name = null)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.IsNotNull(asset, "the probe Prefab must load: " + assetPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(
                asset!, SceneManager.GetActiveScene());
            Assert.IsNotNull(instance, "the fixture instance must be created");
            if (name is not null)
            {
                instance!.name = name;
            }
            _scratch.Add(instance!);
            return instance!;
        }

        // A container Prefab whose root carries a plain child and a nested
        // Prefab instance — the fixture the mode split is proven against.
        private void CreateContainerPrefab()
        {
            PrefabContentsFixture.CreateProbePrefab("NestedBase.prefab");
            var baseAsset =
                AssetDatabase.LoadAssetAtPath<GameObject>(NestedPath);
            Assert.IsNotNull(baseAsset);
            var containerRoot = new GameObject("Container");
            _scratch.Add(containerRoot);
            var plain = new GameObject("Plain");
            plain.transform.SetParent(containerRoot.transform, false);
            var nested = (GameObject)PrefabUtility.InstantiatePrefab(
                baseAsset!, containerRoot.scene);
            _scratch.Add(nested);
            nested.transform.SetParent(containerRoot.transform, false);
            Assert.IsNotNull(
                PrefabUtility.SaveAsPrefabAsset(containerRoot, ContainerPath),
                "the container Prefab must save");
        }

        // A Prefab whose root carries two plain children — the shape the
        // reparenting case needs: the second child moved under the first
        // keeps one and the same depth-first order of entries.
        private void CreateTwoChildPrefab()
        {
            var root = new GameObject("TwoChildren");
            _scratch.Add(root);
            foreach (var name in new[] { "A", "B" })
            {
                var child = new GameObject(name);
                child.transform.SetParent(root.transform, false);
            }
            Assert.IsNotNull(
                PrefabUtility.SaveAsPrefabAsset(root, TwoChildPath),
                "the two-child Prefab must save");
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs) =>
            new UnityStepContext(
                inputs,
                UnpackIn,
                UnpackOut,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "prefab-unpack"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

        private static InputValue Present(string json)
        {
            var parsed = JsonValue.Parse(json);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + json);
            return InputValue.Present(parsed.Value!);
        }

        private static Dictionary<string, InputValue> InstanceInputs(
            GameObject instance, string? mode = null)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["instance"] = InputValue.Present(new RefResolver().ToRef(
                    instance, "unity.PrefabInstanceRef")),
            };
            if (mode is not null)
            {
                inputs["mode"] = Present("\"" + mode + "\"");
            }
            return inputs;
        }

        // The live target's Ref whatever its prefab state — the phase-2
        // re-find publishes a GameObjectRef of the (unpacked) object.
        private static Dictionary<string, InputValue> LiveInputs(
            GameObject live, string? mode = null)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["instance"] = InputValue.Present(new RefResolver().ToRef(
                    live, "unity.GameObjectRef")),
            };
            if (mode is not null)
            {
                inputs["mode"] = Present("\"" + mode + "\"");
            }
            return inputs;
        }

        private static void AssertUnknown(
            ReconcileResult verdict, string fragment)
        {
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => verdict is ReconcileResult.Unknown u
                    ? u.Reason
                    : verdict.GetType().Name);
            Assert.That(((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain(fragment));
        }

        private static JsonValue Restarted(JsonValue snapshot)
        {
            var restarted = (JObject)snapshot.ToToken().DeepClone();
            restarted["editorSessionId"] = "restarted-session";
            return JsonValue.From(restarted);
        }

        // Case 1 — interrupted before Execute: the still-linked instance
        // with the identical structure and override list is provably
        // NotApplied.
        [Test]
        public void Unpack_NotExecuted_ReturnsNotApplied()
        {
            CreateContainerPrefab();
            var instance = Instantiate(ContainerPath);
            var op = new PrefabUnpackOperation();
            var snapshot = op.Preflight(Context(InstanceInputs(instance))).Snapshot;

            var verdict = op.Reconcile(
                Context(InstanceInputs(instance)), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
            Assert.That(
                PrefabUtility.IsOutermostPrefabInstanceRoot(instance),
                Is.True, "an unexecuted step never changes the instance");
        }

        // Case 2 — executed but StepSucceeded not recorded: the same
        // objects in the same places, freed exactly as "outermost" leaves
        // them — Completed with the gameObject output restored.
        [Test]
        public void Unpack_ExecutedUnrecorded_ReturnsCompletedWithGameObject()
        {
            CreateContainerPrefab();
            var instance = Instantiate(ContainerPath);
            var op = new PrefabUnpackOperation();
            var snapshot = op.Preflight(Context(InstanceInputs(instance))).Snapshot;
            var context = Context(InstanceInputs(instance));
            Assert.That(op.Execute(context).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict = op.Reconcile(Context(LiveInputs(instance)), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            Assert.That(outputs.ContainsKey("gameObject"), Is.True);
            Assert.That(
                JToken.DeepEquals(
                    (JObject)outputs["gameObject"].ToToken(),
                    (JObject)context.Outputs["gameObject"].ToToken()),
                Is.True, "restored outputs equal the uninterrupted run's");
        }

        // Case 3a — partial side effect: the snapshot asked for
        // "completely" but only the outermost link went away (a foreign
        // partial unpack — or a crash mid-operation). Neither verdict
        // applies: still-instance check fails on the root, the completed
        // check fails on the remaining nested link.
        [Test]
        public void Unpack_PartialNestedLinkRemaining_ReturnsUnknown()
        {
            CreateContainerPrefab();
            var instance = Instantiate(ContainerPath);
            var op = new PrefabUnpackOperation();
            var snapshot = op.Preflight(
                Context(InstanceInputs(instance, "completely"))).Snapshot;
            PrefabUtility.UnpackPrefabInstance(
                instance, PrefabUnpackMode.OutermostRoot,
                InteractionMode.AutomatedAction);

            var verdict = op.Reconcile(
                Context(LiveInputs(instance, "completely")), snapshot);

            AssertUnknown(verdict, "remains");
            Assert.That(instance, Is.Not.Null, "nothing is deleted");
        }

        // Case 3b — partial side effect: the snapshot asked for
        // "outermost" but a foreign step unpacked the nested link too.
        // The deeper instance's recorded state differs — Unknown.
        [Test]
        public void Unpack_PartialDeeperUnpackThanAsked_ReturnsUnknown()
        {
            CreateContainerPrefab();
            var instance = Instantiate(ContainerPath);
            var op = new PrefabUnpackOperation();
            var snapshot = op.Preflight(Context(InstanceInputs(instance))).Snapshot;
            PrefabUtility.UnpackPrefabInstance(
                instance, PrefabUnpackMode.Completely,
                InteractionMode.AutomatedAction);

            var verdict = op.Reconcile(Context(LiveInputs(instance)), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>());
            Assert.That(instance, Is.Not.Null, "nothing is deleted");
        }

        // Case 4 — a pre-existing same-name artifact is never the step's
        // product: the recorded target was destroyed and a fresh instance
        // of the same Prefab took its place — a new session handle, so the
        // recorded target can never resolve to it.
        [Test]
        public void Unpack_ReplacedSameNameInstance_ReturnsUnknown()
        {
            CreateContainerPrefab();
            var instance = Instantiate(ContainerPath);
            var op = new PrefabUnpackOperation();
            var snapshot = op.Preflight(Context(InstanceInputs(instance))).Snapshot;
            var parent = instance.transform.parent;
            Object.DestroyImmediate(instance);
            _scratch.Remove(instance);
            var replacement = Instantiate(ContainerPath);
            replacement.name = "Container";
            if (parent is not null)
            {
                replacement.transform.SetParent(parent, false);
            }

            var verdict = op.Reconcile(
                Context(LiveInputs(replacement)), snapshot);

            // Either the recorded handle resolves to nothing (the destroyed
            // object is gone) or it resolves to a different object that
            // fails the evidence — never NotApplied, never Completed.
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => verdict is ReconcileResult.Unknown u
                    ? u.Reason
                    : verdict.GetType().Name);
            Assert.That(replacement, Is.Not.Null,
                "the foreign object is never deleted");
        }

        // Case 5 — the artifact was changed by another operation after the
        // step ran: the unpacked hierarchy no longer matches the recorded
        // structure — Unknown, and the object is left untouched.
        [Test]
        public void Unpack_ModifiedArtifact_ReturnsUnknown_AndChangesNothing()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var instance = Instantiate(BasePath);
            var op = new PrefabUnpackOperation();
            var snapshot = op.Preflight(Context(InstanceInputs(instance))).Snapshot;
            Assert.That(op.Execute(Context(InstanceInputs(instance))).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            instance.name = "ForeignRename";

            var verdict = op.Reconcile(Context(LiveInputs(instance)), snapshot);

            AssertUnknown(verdict, "match");
            Assert.That(instance, Is.Not.Null,
                "an Unknown verdict never deletes the object");
        }

        // Case 5b — a reparenting the flat depth-first record cannot see
        // (the §F5 hierarchy configuration): root with sibling children
        // A,B and root with B nested under A produce the identical ordered
        // entry list — same handles, names, components, state — so only
        // the recorded parent identity tells them apart. A foreign
        // reparent after the step ran must end Unknown, never Completed.
        [Test]
        public void Unpack_ReparentedMember_ReturnsUnknown()
        {
            CreateTwoChildPrefab();
            var instance = Instantiate(TwoChildPath);
            var op = new PrefabUnpackOperation();
            var snapshot = op.Preflight(Context(InstanceInputs(instance))).Snapshot;
            Assert.That(op.Execute(Context(InstanceInputs(instance))).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            instance.transform.Find("B")!.SetParent(
                instance.transform.Find("A")!, worldPositionStays: false);

            var verdict = op.Reconcile(Context(LiveInputs(instance)), snapshot);

            AssertUnknown(verdict, "reparented");
            Assert.That(instance, Is.Not.Null,
                "an Unknown verdict never deletes the object");
        }

        // Case 6 — simulated Editor restart: the row's verdicts are
        // same-session ones (the structure entries are session handles), so
        // a different editorSessionId is Unknown even with everything else
        // matching.
        [Test]
        public void Unpack_SimulatedRestart_ReturnsUnknown()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var instance = Instantiate(BasePath);
            var op = new PrefabUnpackOperation();
            var snapshot = op.Preflight(Context(InstanceInputs(instance))).Snapshot;
            Assert.That(op.Execute(Context(InstanceInputs(instance))).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict = op.Reconcile(
                Context(LiveInputs(instance)), Restarted(snapshot));

            AssertUnknown(verdict, "session");
        }

        // A foreign prefab-instance move after the step ran is also a
        // modified artifact: the target is still an instance but its
        // structure or overrides differ — Unknown, never NotApplied.
        [Test]
        public void Unpack_StillInstanceButChanged_ReturnsUnknown()
        {
            PrefabContentsFixture.CreateProbePrefab("Base.prefab");
            var instance = Instantiate(BasePath);
            var op = new PrefabUnpackOperation();
            var snapshot = op.Preflight(Context(InstanceInputs(instance))).Snapshot;
            instance.name = "ForeignOverride";

            var verdict = op.Reconcile(
                Context(InstanceInputs(instance)), snapshot);

            AssertUnknown(verdict, "differ");
            Assert.That(PrefabUtility.IsOutermostPrefabInstanceRoot(instance),
                Is.True, "the foreign override is left in place");
        }

        // ══ [UnityTest] real domain reload (SPEC §T.3) ═══════════════════

        private static object? s_sentinel;
        private static string ProbeDir =>
            Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "OpWeavePrefabUnpackProbe");
        private static string SnapshotPath() =>
            Path.Combine(ProbeDir, "unpack.json");
        private static string SessionProbePath() =>
            Path.Combine(ProbeDir, "session.txt");

        // T-W2.4-09: a per-Editor-session token phase-1 stores in SessionState
        // and writes into the session probe file beside the persisted snapshot
        // (a single JSON document that cannot carry the token); phase-2 reads
        // both to prove the persisted state belongs to this session's phase-1
        // rather than residue of an earlier run.
        private const string ProbeTokenKey = "OpWeave.W2409.PrefabUnpack.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume is
        // detected. leg is a method local, so a real reload resets it; the
        // this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[W2409-unpack] " + message);
        private static string MarkerDir =>
            Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Assets", "OpWeavePrefabUnpackReload");

        private static void PersistSnapshot(JsonValue snapshot)
        {
            var token = System.Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeTokenKey, token);
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(SnapshotPath(), snapshot.CanonicalText);
            File.WriteAllText(SessionProbePath(), token + "\n");
        }

        private static JsonValue ReadSnapshot()
        {
            var parsed = JsonValue.Parse(File.ReadAllText(SnapshotPath()));
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
                Path.Combine(MarkerDir, "PrefabUnpackReloadMarker.cs"),
                "// " + System.Guid.NewGuid().ToString("N") + "\n" +
                "internal static class PrefabUnpackReloadMarker { }\n");
            AssetDatabase.Refresh();
            s_sentinel = new object();
        }

        // Preflight + Execute, persist the snapshot under Library/ and arm
        // the marker compile that forces the domain reload.
        private string PhaseBeforeReload()
        {
            try
            {
                CreateContainerPrefab();
                var instance = Instantiate(ContainerPath, "UnpackTarget");
                var op = new PrefabUnpackOperation();
                var snapshot = op.Preflight(
                    Context(InstanceInputs(instance))).Snapshot;
                if (op.Execute(Context(InstanceInputs(instance))).Kind !=
                    StepResultKind.Succeeded)
                {
                    return "unpack execute failed";
                }
                PersistSnapshot(snapshot);
                ArmReload();
                return string.Empty;
            }
            catch (System.Exception e)
            {
                return "phase-1 threw: " + e.Message;
            }
        }

        // After the reload: nothing is carried in memory — the persisted
        // snapshot is re-read and Reconcile must still reach Completed:
        // the session handles in the structure entries survive the reload
        // (SPEC §T.3).
        private string PhaseAfterReload()
        {
            try
            {
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(SnapshotPath()))
                {
                    return expectedToken.Length == 0
                        ? "phase-1 never executed in this Editor session (" +
                            SnapshotPath() + " was never written; probeDir exists=" +
                            Directory.Exists(ProbeDir) + ")"
                        : "the persisted snapshot " + SnapshotPath() +
                            " was deleted after phase-1 wrote it";
                }
                if (expectedToken.Length == 0 || !File.Exists(SessionProbePath()) ||
                    File.ReadAllText(SessionProbePath()).Trim() != expectedToken)
                {
                    return "the persisted probe state is stale — a previous " +
                        "Editor session wrote " + SessionProbePath();
                }
                var snapshot = ReadSnapshot();
                var live = GameObject.Find("UnpackTarget");
                if (live == null)
                {
                    return "the unpack target is gone after the reload";
                }
                var verdict = new PrefabUnpackOperation().Reconcile(
                    Context(LiveInputs(live)), snapshot);
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

        // A failed phase must never poison the next test: the UnitySetUp /
        // UnityTearDown pair sweeps the persisted snapshot, the marker
        // script and the probe assets whatever the test asserted (the
        // PrefabCreateReconcileTests pattern). Setup and teardown must never
        // yield an ExpectDomainReload instruction (RecompileScripts): UTF's
        // PrepareForDomainReload records the CURRENT TestEnumerator's program
        // counter — still a different test's method enumerator during setup
        // and teardown — and the resume then applies that stale PC to the
        // next test method's fresh enumerator, jumping it past phase-1
        // entirely when the captured enumerator was abandoned mid-state. The
        // marker deletion queues its compile under the runner's
        // LockReloadAssemblies, so the reload it needs drains at the next
        // test body's own `yield return new RecompileScripts()`, whose
        // captured PC is the correct one.
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

        private static bool CleanupReloadArtifacts()
        {
            var dirty = false;
            foreach (var path in new[] { BasePath, NestedPath, ContainerPath })
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
            foreach (var strayName in new[] { "UnpackTarget", "Container" })
            {
                var stray = GameObject.Find(strayName);
                if (stray != null)
                {
                    Object.DestroyImmediate(stray);
                }
            }
            var markerAsset = "Assets/OpWeavePrefabUnpackReload";
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

        // The phase-1 probe proves phase-1 ran in THIS Editor session: the
        // session file must exist AND its content must equal the token phase-1
        // stored in SessionState. Both Library/ and SessionState survive the
        // reload, so a stale-resume hijack (phase-1 skipped: no file, no token)
        // and residue of an earlier session (file yes, token no) both fail the
        // check — the caller then re-drives a real phase-1 instead of skipping
        // into phase-2.
        private static bool ProbeWrittenThisSession()
        {
            if (!File.Exists(SnapshotPath()) || !File.Exists(SessionProbePath()))
            {
                return false;
            }
            var expectedToken = SessionState.GetString(ProbeTokenKey, "");
            return expectedToken.Length > 0 &&
                File.ReadAllText(SessionProbePath()).Trim() == expectedToken;
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Unpack_Reconcile_AfterRealDomainReload_Completed()
        {
            var before = PhaseBeforeReload();
            Crumb("phase-1 returned: " + before);
            Assert.That(before, Is.Empty, "pre-reload phase failed");

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
                while (s_sentinel is not null && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsNull(s_sentinel,
                    "the marker compile did not produce a domain reload");

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                // A marker file an abandoned leg left behind carries identical
                // bytes, so deleting the probe assets guarantees the re-write
                // queues a real compile rather than a no-op Refresh.
                CleanupReloadArtifacts();
                var redrive = PhaseBeforeReload();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " -> " + redrive);
                Assert.That(redrive, Is.Empty,
                    "re-driven pre-reload phase failed: " + redrive);
            }

            var after = PhaseAfterReload();
            Crumb("phase-2 returned: " + after);
            Assert.That(after, Is.Empty, "post-reload phase failed");
        }
    }
}
