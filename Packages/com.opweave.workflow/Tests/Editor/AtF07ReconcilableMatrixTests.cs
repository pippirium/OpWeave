#nullable enable

// T-W4.6-04 [Edit] tests: local:T-W4.6-04:atf07-reconcilable-20x6 — cases 1-5
// of the SPEC §F5 six-case table for every one of the 20 standard
// Reconcilable Operations (SPEC §I), driven in-domain through the real
// OpWeaveRunnerHost + FileRunStore + §F4 recovery engine the T-W4.3-03
// fixtures wire: a Run parked mid-flight, its journal cut back to the bare
// step_started{step:0} — case 1 restores the provable pre-state, case 2
// leaves the applied product, case 3 leaves a partial effect, case 4 adds
// the pre-existing same-name artifact, case 5 changes the artifact by
// another operation — then resumed through recovery. §F5 prescribes the
// verdict of every cell: Completed only when the recorded evidence row
// holds, otherwise NeedsReview — never an automatic success, never an
// automatic re-execution, and zero duplicate or mis-deleted artifacts.
// Case 6 (a real Editor restart) lives in AtF07RestartMatrixTests and the
// arrange-matrix / verify-matrix child scenarios.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Recovery;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter.Tests
{
    [TestFixture]
    internal sealed class AtF07ReconcilableMatrixTests
    {
        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();

        private string? _key;
        private int _case;
        private string? _runId;
        private GameObject? _saveContents;
        private string? _saveGuid;
        private (string tagged, string head) _git;
        // package.add case 1's world state: the recovery judgement's first
        // channel read sees the restored pre-state; the re-issued add
        // request then resolves and every later read sees it installed.
        private bool _addResolved;

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
            PrefabContentsFixture.UnloadAllOpenedContents();
            AtF07Matrix.EnsureBaseScene();
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (go.name.StartsWith("Atf07M", StringComparison.Ordinal))
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            var folder = AtF07Matrix.Folder;
            if (AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.DeleteAsset(folder);
                // DeleteAsset only marks the removal: in batchmode the
                // files can still be on disk until the asset pipeline
                // settles, and a raw File.Copy (the restart fixture's
                // project clone) would capture the ghosts as residue
                // (T-W2.4-11). Refresh flushes the delete to disk
                // synchronously.
                AssetDatabase.Refresh();
            }
        }

        // ── Fixtures ────────────────────────────────────────────────────

        // The per-case Run Store root (SPEC §E2): a shared root lets the
        // previous case's still-active parked Run and its lock.json owner
        // record make the next fixture's new Run wait behind a foreign
        // live owner — the "left Running" queue block. One root per
        // (Operation, case) keeps every parked Run alone on its store;
        // the second fixture of a case reuses it for the recovery pass.
        private string Store => AtF07Matrix.StoreRootFor(_key! + "-case" + _case);

        // A previous Unity run's residue on the per-case root: a stale
        // lock.json owner record makes Accept's OS exclusion probe refuse
        // and a persisted unfinished Run makes recovery fold foreign
        // journals — take the dead exclusion over the hand-off seam and
        // drop the whole root so each case's store is truly fresh.
        private static void CleanStoreRoot(string root)
        {
            var obtained = RunLockHandoff.TryObtain(root);
            if (obtained.Ok && obtained.Value is { } held)
            {
                if (!held.Release().Ok)
                {
                    held.Dispose();
                }
            }
            RunLockHandoff.Clear(root);
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
        }

        private RunnerHostFixture NewFixture(
            Func<PackageInstallView> addView,
            Func<PackageInstallView> removeView)
        {
            var fixture = new RunnerHostFixture(Store);
            _fixtures.Add(fixture);
            AtF07Matrix.RegisterAll(fixture, addView, removeView);
            fixture.ReleaseDispatcher.Register(
                PrefabContentsResources.Kind, PrefabContentsResources.Release);
            return fixture;
        }

        // The parked Run's pre-state channels: package.remove's snapshot
        // must record the installed package; package.add's records the
        // absent state (or, for case 4, the already-installed one).
        private RunnerHostFixture Fixture1(MatrixOp op) =>
            NewFixture(
                op.Key == "package.add"
                    ? (_case == 4 && op.C4AddView != null
                        ? op.C4AddView : AtF07Matrix.AbsentView)
                    : AtF07Matrix.AbsentView,
                op.Key == "package.remove"
                    ? () => GitView(_git.tagged)
                    : AtF07Matrix.AbsentView);

        // The state the recovery judgement reads per case.
        private RunnerHostFixture Fixture2(MatrixOp op)
        {
            var git = op.Key == "package.add" || op.Key == "package.remove";
            switch (op.Key)
            {
                case "package.add":
                    return NewFixture(
                        _case switch
                        {
                            1 => () =>
                            {
                                var first = !_addResolved;
                                _addResolved = true;
                                return first
                                    ? AtF07Matrix.AbsentView()
                                    : GitView(_git.tagged);
                            },
                            2 or 4 => (Func<PackageInstallView>)(() => GitView(_git.tagged)),
                            3 => () => AtF07Matrix.PartialAddView(Dep),
                            5 => () => GitView(_git.head),
                            _ => AtF07Matrix.AbsentView,
                        },
                        AtF07Matrix.AbsentView);
                case "package.remove":
                    return NewFixture(
                        AtF07Matrix.AbsentView,
                        _case switch
                        {
                            1 => (Func<PackageInstallView>)(() => GitView(_git.tagged)),
                            3 => () => AtF07Matrix.PartialRemoveView(
                                Dep, _git.tagged, _git.tagged),
                            4 => AtF07Matrix.RegistryInstalledView,
                            5 => () => GitView(_git.head),
                            _ => AtF07Matrix.AbsentView,
                        });
                default:
                    return NewFixture(AtF07Matrix.AbsentView, AtF07Matrix.AbsentView);
            }
        }

        private PackageInstallView GitView(string commit) =>
            AtF07Matrix.GitInstalledView(Dep, commit, _git.tagged);

        // The dep requests a full commit SHA — a tag or branch request can
        // never carry the persisted expected commit §G8.1 requires for
        // Completed (NoExpectedCommit → Unknown), so the matrix's Git dep
        // names the tagged commit's SHA directly.
        private string Dep => AtF07Matrix.GitDep(_git.tagged);

        private static IEnumerable<string> Ops() => AtF07Matrix.OpKeys;

        private void RunCase(string key, int caseNo)
        {
            _key = key;
            _case = caseNo;
            AtF07Matrix.EnsureBaseScene();
            var root = Store;
            CleanStoreRoot(root);
            var op = Build(key);
            if (caseNo == 4)
            {
                op.C4Prep?.Invoke();
            }
            if (caseNo == 4 && op.C4Refused)
            {
                var f0 = Fixture1(op);
                op.Prep();
                var (plan0, doc0) = PlanFor(op);
                var runId0 = AtF07Matrix.Accept(f0, plan0, doc0);
                AtF07Matrix.Tick(f0);
                Assert.IsFalse(
                    RunnerHostFixture.JournalHas(
                        root, runId0, "\"type\":\"step_started\""),
                    key + " case-4: a refused Preflight never journals step_started");
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        root, runId0, "\"type\":\"step_failed\"", "\"step\":0"),
                    key + " case-4: the refused step is journaled step_failed");
                op.AfterC4?.Invoke();
                return;
            }
            var f1 = Fixture1(op);
            op.Prep();
            _runId = AtF07Matrix.Park(f1, op);
            op.Applied();
            AtF07Matrix.TruncateStarted(root, _runId);
            _fixtures.Remove(f1);
            f1.Dispose();
            switch (caseNo)
            {
                case 1: op.Unapply?.Invoke(); break;
                case 3: op.C3?.Invoke(); break;
                case 4: op.C4Unapply?.Invoke(); break;
                case 5:
                    op.C5?.Invoke();
                    // The package channel of case 5: the local fixture tag
                    // moves to a different commit — the installed state the
                    // recovery judgement then reads contradicts the
                    // recorded commit (CommitMismatch/Changed → Unknown).
                    if (key == "package.add" || key == "package.remove")
                    {
                        AtF07Matrix.MoveTagToHead();
                    }
                    break;
            }
            var f2 = Fixture2(op);
            var report = AtF07Matrix.Recover(f2);
            Assert.IsTrue(report.ExclusionHeld);
            AtF07Matrix.Settle(f2, _runId);
            var verdict = caseNo switch
            {
                1 => op.C1,
                2 => op.C2,
                3 => MatrixVerdict.Review,
                4 => op.C4,
                _ => MatrixVerdict.Review,
            };
            var because = key + " case-" + caseNo;
            if (verdict == MatrixVerdict.Reexec)
            {
                AtF07Matrix.AssertReexecuted(root, f2, _runId, because);
            }
            else if (verdict == MatrixVerdict.Adopted)
            {
                AtF07Matrix.AssertAdopted(root, f2, _runId, because);
            }
            else
            {
                AtF07Matrix.AssertNeedsReview(root, f2, _runId, because);
            }
            switch (caseNo)
            {
                case 1: op.AfterC1?.Invoke(); break;
                case 2: op.AfterC2?.Invoke(); break;
                case 3: op.AfterC3?.Invoke(); break;
                case 4: op.AfterC4?.Invoke(); break;
                case 5: op.AfterC5?.Invoke(); break;
            }
        }

        private static (CompiledPlan plan, WorkflowDocument doc) PlanFor(MatrixOp op)
        {
            var steps = new[]
            {
                AtF07Matrix.Step(0, "s", op.OpId, op.Seed, next: 1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: op.Inputs(), outputTypes: op.Outputs),
                AtF07Matrix.Step(1, "h", AtF07Matrix.HoldOp, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            return (RunnerHostFixture.Plan(AtF07Matrix.WorkflowId, steps),
                AtF07Matrix.Doc(
                    new[] { ("s", op.OpId), ("h", AtF07Matrix.HoldOp) },
                    ("s", "h")));
        }

        // ══ SPEC §F5 case 1: journal cut at StepStarted.
        [TestCaseSource(nameof(Ops))]
        public void Case1_JournalCutAtStepStarted(string key) => RunCase(key, 1);

        // ══ SPEC §F5 case 2: journal cut after Execute.
        [TestCaseSource(nameof(Ops))]
        public void Case2_JournalCutAfterExecute(string key) => RunCase(key, 2);

        // ══ SPEC §F5 case 3: partial side effect.
        [TestCaseSource(nameof(Ops))]
        public void Case3_PartialSideEffect(string key) => RunCase(key, 3);

        // ══ SPEC §F5 case 4: pre-existing same-name artifact.
        [TestCaseSource(nameof(Ops))]
        public void Case4_PreexistingSameNameArtifact(string key) => RunCase(key, 4);

        // ══ SPEC §F5 case 5: artifact changed by another operation.
        [TestCaseSource(nameof(Ops))]
        public void Case5_ArtifactChangedByAnotherOperation(string key) =>
            RunCase(key, 5);

        // ── The twenty-Operation spec table ─────────────────────────────

        private static Constant Cst(JsonValue value) => new Constant(value);
        private static Constant Cst(string jsonText) =>
            new Constant(RunnerHostFixture.Json(jsonText));
        private static Dictionary<string, string> Out(params (string, string)[] p) =>
            p.ToDictionary(x => x.Item1, x => x.Item2);
        private static List<GameObject> Named(string name) =>
            UnityEngine.Object.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(g => g.name == name).ToList();

        private static string ProbeShader() =>
            "Shader \"OpWeave/Atf07Probe\"\n{\n" +
            "    Properties { _Color (\"Color\", Color) = (1,1,1,1) }\n" +
            "    SubShader { Pass { } }\n}\n";

        private MatrixOp Build(string key)
        {
            switch (key)
            {
                case "scene.create":
                {
                    var path = AtF07Matrix.P(key, "Created.unity");
                    return new MatrixOp
                    {
                        Key = key, OpId = CreateSceneOperation.OperationId, Seed = 'a',
                        Prep = () => AtF07Matrix.EnsureFolder(
                            Path.GetDirectoryName(path)!.Replace('\\', '/')),
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["path"] = Cst("\"" + path + "\""),
                            ["setup"] = Cst("\"empty\""),
                            ["mode"] = Cst("\"additive\""),
                        },
                        Outputs = Out(("scene", "unity.SceneRef")),
                        Applied = () => Assert.IsTrue(
                            SceneManager.GetSceneByPath(path).isLoaded,
                            "scene.create did not load its scene"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () =>
                        {
                            var s = SceneManager.GetSceneByPath(path);
                            if (s.isLoaded)
                            {
                                EditorSceneManager.CloseScene(s, true);
                            }
                            AssetDatabase.DeleteAsset(path);
                        },
                        AfterC1 = () => Assert.IsTrue(
                            SceneManager.GetSceneByPath(path).isLoaded,
                            "the re-executed scene.create did not load its scene"),
                        C3 = () => EditorSceneManager.CloseScene(
                            SceneManager.GetSceneByPath(path), true),
                        AfterC3 = () =>
                        {
                            Assert.IsNotNull(
                                AssetDatabase.LoadAssetAtPath<SceneAsset>(path),
                                "NeedsReview removed nothing — the asset stays");
                            Assert.IsFalse(
                                SceneManager.GetSceneByPath(path).isLoaded,
                                "NeedsReview re-opened nothing");
                        },
                        C4 = MatrixVerdict.Review,
                        C4Unapply = () =>
                        {
                            var s = SceneManager.GetSceneByPath(path);
                            if (s.isLoaded)
                            {
                                EditorSceneManager.CloseScene(s, true);
                            }
                            AssetDatabase.DeleteAsset(path);
                            var foreign = EditorSceneManager.NewScene(
                                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                            SceneManager.SetActiveScene(foreign);
                            new GameObject("Atf07MForeignRoot");
                            EditorSceneManager.SaveScene(foreign, path);
                            EditorSceneManager.CloseScene(foreign, true);
                        },
                        AfterC4 = () => Assert.IsNotNull(
                            AssetDatabase.LoadAssetAtPath<SceneAsset>(path),
                            "NeedsReview removed the foreign same-path scene"),
                        C5 = () =>
                        {
                            var s = EditorSceneManager.OpenScene(
                                path, OpenSceneMode.Additive);
                            new GameObject("Atf07MForeignRoot");
                            EditorSceneManager.MarkSceneDirty(s);
                            EditorSceneManager.SaveScene(s);
                        },
                    };
                }

                case "go.create":
                {
                    const string name = "Atf07MGoCreate";
                    GameObject? preExisting = null;
                    return new MatrixOp
                    {
                        Key = key, OpId = GameObjectCreateOperation.OperationId, Seed = 'c',
                        Inputs = () => new Dictionary<string, InputSource>
                            { ["name"] = Cst("\"" + name + "\"") },
                        Outputs = Out(("gameObject", "unity.GameObjectRef")),
                        Applied = () => Assert.IsNotNull(
                            AtF07Matrix.FindByName(name), "go.create did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => UnityEngine.Object.DestroyImmediate(
                            AtF07Matrix.FindByName(name)),
                        AfterC1 = () => Assert.AreEqual(
                            1, AtF07Matrix.CountByName(name),
                            "the re-executed go.create did not leave exactly one"),
                        C3 = () => new GameObject("Atf07MForeign"),
                        AfterC3 = () =>
                        {
                            Assert.AreEqual(1, AtF07Matrix.CountByName(name));
                            Assert.AreEqual(1, AtF07Matrix.CountByName("Atf07MForeign"),
                                "NeedsReview removed neither object");
                        },
                        C4 = MatrixVerdict.Reexec,
                        C4Prep = () => preExisting = new GameObject(name),
                        C4Unapply = () =>
                        {
                            foreach (var g in Named(name))
                            {
                                if (!ReferenceEquals(g, preExisting))
                                {
                                    UnityEngine.Object.DestroyImmediate(g);
                                }
                            }
                        },
                        AfterC4 = () => Assert.AreEqual(
                            2, AtF07Matrix.CountByName(name),
                            "re-execution leaves the pre-existing artifact and " +
                            "exactly one product of its own"),
                        C5 = () => AtF07Matrix.FindByName(name).name = "Atf07MRenamed",
                        AfterC5 = () => Assert.IsNotNull(
                            AtF07Matrix.FindByName("Atf07MRenamed"),
                            "NeedsReview removed nothing"),
                    };
                }

                case "go.delete":
                {
                    const string name = "Atf07MGoDelete";
                    GameObject? target = null;
                    return new MatrixOp
                    {
                        Key = key, OpId = GameObjectDeleteOperation.OperationId, Seed = 'e',
                        Prep = () =>
                        {
                            target = new GameObject(name);
                            new GameObject("Atf07MDelSibling");
                        },
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["target"] = Cst(new RefResolver().ToRef(
                                target!, "unity.GameObjectRef")),
                        },
                        Outputs = Out(),
                        Applied = () => Assert.AreEqual(
                            _case == 4 ? 1 : 0, AtF07Matrix.CountByName(name),
                            "go.delete did not run"),
                        // The deleted object's session identity cannot be
                        // restored at the Runner boundary — the unapplied
                        // row of case 1 is unprovable here, so §F5 prescribes
                        // Unknown → NeedsReview.
                        C1 = MatrixVerdict.Review,
                        Unapply = () => new GameObject(name),
                        AfterC1 = () => Assert.AreEqual(
                            1, AtF07Matrix.CountByName(name),
                            "NeedsReview never deletes the foreign same-name object"),
                        C3 = () => UnityEngine.Object.DestroyImmediate(
                            AtF07Matrix.FindByName("Atf07MDelSibling")),
                        AfterC3 = () => Assert.IsNull(
                            AtF07Matrix.FindByName("Atf07MDelSibling"),
                            "NeedsReview restored the extra deletion"),
                        C4 = MatrixVerdict.Adopted,
                        C4Prep = () => new GameObject(name),
                        AfterC4 = () =>
                        {
                            Assert.AreEqual(
                                1, AtF07Matrix.CountByName(name),
                                "the pre-existing same-name artifact must stay " +
                                "— exactly one object of the name may remain");
                            Assert.IsNotNull(
                                AtF07Matrix.FindByName("Atf07MDelSibling"),
                                "the pre-existing sibling was mis-deleted");
                        },
                        C5 = () => new GameObject(name),
                        AfterC5 = () => Assert.AreEqual(
                            1, AtF07Matrix.CountByName(name),
                            "NeedsReview never deletes the foreign same-name object"),
                    };
                }

                case "component.add":
                {
                    const string name = "Atf07MCompAdd";
                    return new MatrixOp
                    {
                        Key = key, OpId = ComponentAddOperation.OperationId, Seed = '0',
                        Prep = () =>
                        {
                            var go = new GameObject(name);
                            if (_case == 4)
                            {
                                go.AddComponent<BoxCollider>();
                            }
                        },
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["target"] = Cst(RunnerHostFixture.Json(
                                "{\"kind\":\"gameObject\",\"path\":\"/" + name + "\"}")),
                            ["componentType"] = Cst("\"UnityEngine.BoxCollider\""),
                        },
                        Outputs = Out(("component", "unity.ComponentRef<unity.Component>")),
                        Applied = () => Assert.AreEqual(
                            _case == 4 ? 2 : 1,
                            AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>().Length,
                            "component.add did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => UnityEngine.Object.DestroyImmediate(
                            AtF07Matrix.FindByName(name).GetComponent<BoxCollider>()),
                        AfterC1 = () => Assert.AreEqual(
                            1, AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>().Length,
                            "the re-executed add did not leave exactly one"),
                        C3 = () => AtF07Matrix.FindByName(name).AddComponent<Rigidbody>(),
                        AfterC3 = () => Assert.AreEqual(
                            1, AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>().Length,
                            "NeedsReview adds nothing and removes nothing"),
                        C4 = MatrixVerdict.Reexec,
                        C4Unapply = () =>
                        {
                            var bcs = AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>();
                            UnityEngine.Object.DestroyImmediate(bcs[bcs.Length - 1]);
                        },
                        AfterC4 = () => Assert.AreEqual(
                            2, AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>().Length,
                            "re-execution leaves the pre-existing component and " +
                            "exactly one addition of its own"),
                        C5 = () => AtF07Matrix.FindByName(name)
                            .GetComponent<BoxCollider>().size = Vector3.one * 7f,
                        AfterC5 = () => Assert.AreEqual(
                            1, AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>().Length,
                            "NeedsReview duplicated the component"),
                    };
                }

                case "component.remove":
                {
                    const string name = "Atf07MCompRemove";
                    BoxCollider? second = null;
                    return new MatrixOp
                    {
                        Key = key, OpId = ComponentRemoveOperation.OperationId, Seed = '2',
                        Prep = () =>
                        {
                            var go = new GameObject(name);
                            go.AddComponent<BoxCollider>();
                            second = go.AddComponent<BoxCollider>();
                        },
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["component"] = Cst(new RefResolver().ToRef(
                                second!, "unity.ComponentRef<unity.Component>")),
                        },
                        Outputs = Out(),
                        Applied = () => Assert.AreEqual(
                            1, AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>().Length,
                            "component.remove did not run"),
                        // The removed component's session identity cannot be
                        // restored — case 1's unapplied row is unprovable,
                        // so the verdict is Unknown → NeedsReview.
                        C1 = MatrixVerdict.Review,
                        Unapply = () => AtF07Matrix.FindByName(name)
                            .AddComponent<Rigidbody>(),
                        C3 = () => UnityEngine.Object.DestroyImmediate(
                            AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>()[0]),
                        AfterC3 = () => Assert.IsNull(
                            AtF07Matrix.FindByName(name).GetComponent<BoxCollider>(),
                            "NeedsReview restored a removed component"),
                        C4 = MatrixVerdict.Adopted,
                        AfterC4 = () => Assert.AreEqual(
                            1, AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>().Length,
                            "the pre-existing same-type component was mis-deleted"),
                        C5 = () => AtF07Matrix.FindByName(name).AddComponent<BoxCollider>(),
                        AfterC5 = () => Assert.AreEqual(
                            2, AtF07Matrix.FindByName(name)
                                .GetComponents<BoxCollider>().Length,
                            "NeedsReview neither restores nor removes"),
                    };
                }

                case "prefab.create_from":
                {
                    var path = AtF07Matrix.P(key, "FromGo.prefab");
                    string? preGuid = null; string? preBytes = null; string? preMeta = null;
                    return new MatrixOp
                    {
                        Key = key,
                        OpId = PrefabCreateFromGameObjectOperation.OperationId, Seed = 'b',
                        Prep = () =>
                        {
                            AtF07Matrix.EnsureFolder(
                                Path.GetDirectoryName(path)!.Replace('\\', '/'));
                            new GameObject("Atf07MSource");
                            if (_case == 4)
                            {
                                preGuid = AtF07Matrix.CreatePrefab(path, "Atf07MPre");
                                preBytes = File.ReadAllText(AtF07Matrix.Disk(path));
                                preMeta = File.ReadAllText(
                                    AtF07Matrix.Disk(path) + ".meta");
                            }
                        },
                        Inputs = () =>
                        {
                            var inputs = new Dictionary<string, InputSource>
                            {
                                ["source"] = Cst(RunnerHostFixture.Json(
                                    "{\"kind\":\"gameObject\",\"path\":\"/Atf07MSource\"}")),
                                ["path"] = Cst("\"" + path + "\""),
                                ["connect"] = Cst("false"),
                            };
                            if (preGuid != null)
                            {
                                inputs["overwrite"] = Cst("true");
                            }
                            return inputs;
                        },
                        Outputs = Out(("prefab", "unity.PrefabAssetRef")),
                        Applied = () => Assert.IsNotNull(
                            AssetDatabase.LoadAssetAtPath<GameObject>(path),
                            "create_from_gameobject did not write the prefab"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => AssetDatabase.DeleteAsset(path),
                        AfterC1 = () => Assert.IsNotNull(
                            AssetDatabase.LoadAssetAtPath<GameObject>(path),
                            "the re-executed create wrote no prefab"),
                        // A file that is not a Prefab — the payload the
                        // torn write left never imports as the product.
                        C3 = () => AtF07Matrix.ForeignFile(
                            path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"),
                        C4 = MatrixVerdict.Reexec,
                        C4Unapply = () =>
                        {
                            File.WriteAllText(AtF07Matrix.Disk(path), preBytes!);
                            File.WriteAllText(
                                AtF07Matrix.Disk(path) + ".meta", preMeta!);
                            AssetDatabase.Refresh();
                        },
                        AfterC4 = () => Assert.AreEqual(
                            preGuid, AssetDatabase.AssetPathToGUID(path),
                            "re-execution overwrote the recorded guid — it " +
                            "never duplicated the artifact"),
                        // A foreign prefab whose content actually differs —
                        // a bare foreign GameObject carries the same
                        // normalized fingerprint as the recorded product
                        // (the root name normalizes to the file stem), so
                        // the collision never reached the evidence row.
                        C5 = () =>
                        {
                            AssetDatabase.DeleteAsset(path);
                            var foreign = new GameObject("Atf07MForeign");
                            foreign.AddComponent<BoxCollider>();
                            try
                            {
                                Assert.IsNotNull(
                                    PrefabUtility.SaveAsPrefabAsset(foreign, path),
                                    "the foreign prefab must write");
                            }
                            finally
                            {
                                UnityEngine.Object.DestroyImmediate(foreign);
                            }
                            AssetDatabase.Refresh();
                        },
                        AfterC5 = () => Assert.IsNotNull(
                            AssetDatabase.LoadAssetAtPath<GameObject>(path),
                            "NeedsReview removed the foreign prefab"),
                    };
                }

                case "prefab.instantiate":
                {
                    var basePath = AtF07Matrix.P(key, "Atf07MBase.prefab");
                    string? baseGuid = null;
                    GameObject? preInstance = null;
                    return new MatrixOp
                    {
                        Key = key, OpId = PrefabInstantiateOperation.OperationId, Seed = 'd',
                        Prep = () =>
                        {
                            baseGuid = AtF07Matrix.CreatePrefab(basePath, "Atf07MBase");
                            if (_case == 4)
                            {
                                preInstance = (GameObject)PrefabUtility.InstantiatePrefab(
                                    AssetDatabase.LoadAssetAtPath<GameObject>(basePath));
                            }
                        },
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["prefab"] = Cst(PrefabContentsFixture.PrefabRef(
                                baseGuid!, basePath)),
                        },
                        Outputs = Out(("instance", "unity.PrefabInstanceRef")),
                        Applied = () => Assert.AreEqual(
                            _case == 4 ? 2 : 1, Named("Atf07MBase").Count,
                            "instantiate did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => UnityEngine.Object.DestroyImmediate(
                            Named("Atf07MBase").First()),
                        AfterC1 = () => Assert.AreEqual(
                            1, Named("Atf07MBase").Count,
                            "the re-executed instantiate left no instance"),
                        C3 = () => PrefabUtility.InstantiatePrefab(
                            AssetDatabase.LoadAssetAtPath<GameObject>(basePath)),
                        AfterC3 = () => Assert.AreEqual(
                            2, Named("Atf07MBase").Count,
                            "NeedsReview removed neither instance"),
                        C4 = MatrixVerdict.Reexec,
                        C4Unapply = () =>
                        {
                            foreach (var g in Named("Atf07MBase"))
                            {
                                if (!ReferenceEquals(g, preInstance))
                                {
                                    UnityEngine.Object.DestroyImmediate(g);
                                }
                            }
                        },
                        AfterC4 = () => Assert.AreEqual(
                            2, Named("Atf07MBase").Count,
                            "re-execution leaves the pre-existing instance and " +
                            "exactly one of its own"),
                        C5 = () => Named("Atf07MBase").First().AddComponent<BoxCollider>(),
                        AfterC5 = () => Assert.AreEqual(
                            1, Named("Atf07MBase").Count,
                            "NeedsReview duplicated the instance"),
                    };
                }

                case "prefab.unpack":
                {
                    var basePath = AtF07Matrix.P(key, "Atf07MUnpack.prefab");
                    GameObject? instance = null;
                    GameObject? preObject = null;
                    return new MatrixOp
                    {
                        Key = key, OpId = PrefabUnpackOperation.OperationId, Seed = 'f',
                        Prep = () =>
                        {
                            AtF07Matrix.CreatePrefab(basePath, "Atf07MUnpack");
                            instance = (GameObject)PrefabUtility.InstantiatePrefab(
                                AssetDatabase.LoadAssetAtPath<GameObject>(basePath));
                            if (_case == 4)
                            {
                                preObject = new GameObject("Atf07MUnpack");
                            }
                        },
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["instance"] = Cst(new RefResolver().ToRef(
                                instance!, "unity.PrefabInstanceRef")),
                        },
                        Outputs = Out(("gameObject", "unity.GameObjectRef")),
                        Applied = () => Assert.IsFalse(
                            PrefabUtility.IsAnyPrefabInstanceRoot(instance),
                            "unpack did not run"),
                        C1 = MatrixVerdict.Review,
                        Unapply = () =>
                        {
                            UnityEngine.Object.DestroyImmediate(instance);
                            PrefabUtility.InstantiatePrefab(
                                AssetDatabase.LoadAssetAtPath<GameObject>(basePath));
                        },
                        C3 = () =>
                        {
                            var child = new GameObject("Atf07MForeignChild");
                            child.transform.SetParent(instance!.transform, false);
                        },
                        AfterC3 = () => Assert.IsNotNull(
                            AtF07Matrix.FindByName("Atf07MForeignChild"),
                            "NeedsReview removed the foreign child"),
                        C4 = MatrixVerdict.Review,
                        C4Unapply = () =>
                        {
                            foreach (var g in Named("Atf07MUnpack"))
                            {
                                if (!ReferenceEquals(g, preObject))
                                {
                                    UnityEngine.Object.DestroyImmediate(g);
                                }
                            }
                        },
                        AfterC4 = () => Assert.AreEqual(
                            1, AtF07Matrix.CountByName("Atf07MUnpack"),
                            "the pre-existing same-name object was disturbed"),
                        C5 = () => instance!.AddComponent<BoxCollider>(),
                    };
                }

                case "prefab.create_variant":
                {
                    var basePath = AtF07Matrix.P(key, "VariantBase.prefab");
                    var path = AtF07Matrix.P(key, "Variant.prefab");
                    string? baseGuid = null;
                    return new MatrixOp
                    {
                        Key = key,
                        OpId = PrefabCreateVariantOperation.OperationId, Seed = 'h',
                        Prep = () =>
                        {
                            AtF07Matrix.EnsureFolder(
                                Path.GetDirectoryName(path)!.Replace('\\', '/'));
                            baseGuid = AtF07Matrix.CreatePrefab(basePath, "Atf07MVBase");
                        },
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["base"] = Cst(PrefabContentsFixture.PrefabRef(
                                baseGuid!, basePath)),
                            ["path"] = Cst("\"" + path + "\""),
                        },
                        Outputs = Out(("variant", "unity.PrefabAssetRef")),
                        Applied = () => Assert.IsNotNull(
                            AssetDatabase.LoadAssetAtPath<GameObject>(path),
                            "create_variant did not write the variant"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => AssetDatabase.DeleteAsset(path),
                        C3 = () => AtF07Matrix.ForeignFile(
                            path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"),
                        C4Refused = true,
                        C4Prep = () =>
                        {
                            if (_case == 4)
                            {
                                AtF07Matrix.EnsureFolder(
                                    Path.GetDirectoryName(path)!.Replace('\\', '/'));
                                AtF07Matrix.CreatePrefab(path, "Atf07MOccupied");
                            }
                        },
                        AfterC4 = () => Assert.IsNotNull(
                            AssetDatabase.LoadAssetAtPath<GameObject>(path),
                            "the refused Preflight disturbed the occupied artifact"),
                        C5 = () =>
                        {
                            AssetDatabase.DeleteAsset(path);
                            AtF07Matrix.CreatePrefab(path, "Atf07MForeign");
                        },
                    };
                }

                case "prefab.open_contents":
                {
                    var path = AtF07Matrix.P(key, "OpenMe.prefab");
                    string? guid = null;
                    return new MatrixOp
                    {
                        Key = key,
                        OpId = "unity.prefab.open_contents", Seed = 'j',
                        Prep = () =>
                        {
                            guid = AtF07Matrix.CreatePrefab(path, "Atf07MOpen");
                            if (_case == 4)
                            {
                                PrefabUtility.LoadPrefabContents(path);
                            }
                        },
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["prefab"] = Cst(PrefabContentsFixture.PrefabRef(guid!, path)),
                        },
                        Outputs = Out(("root", "unity.GameObjectRef")),
                        Applied = () => Assert.AreEqual(
                            _case == 4 ? 2 : 1,
                            PrefabContentsResources.FindEditingRoots(path).Count,
                            "open_contents did not open a session"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => PrefabContentsFixture.UnloadAllOpenedContents(),
                        AfterC1 = () => Assert.AreEqual(
                            1, PrefabContentsResources.FindEditingRoots(path).Count,
                            "the re-executed open_contents must own exactly one " +
                            "session"),
                        // The resource_acquired record is cut with the
                        // journal tail — the live session can never be
                        // attributed to this step, so case 2's applied row
                        // is unproven: Unknown → NeedsReview.
                        C2 = MatrixVerdict.Review,
                        AfterC2 = () => Assert.AreEqual(
                            1, PrefabContentsResources.FindEditingRoots(path).Count,
                            "NeedsReview closed or duplicated the live session"),
                        C3 = () =>
                        {
                            var p2 = AtF07Matrix.P(key, "Other.prefab");
                            AtF07Matrix.CreatePrefab(p2, "Atf07MOther");
                            PrefabUtility.LoadPrefabContents(p2);
                        },
                        AfterC3 = () => Assert.AreEqual(
                            1, PrefabContentsResources.FindEditingRoots(path).Count,
                            "NeedsReview closed the step's session"),
                        C4 = MatrixVerdict.Review,
                        AfterC4 = () => Assert.AreEqual(
                            2, PrefabContentsResources.FindEditingRoots(path).Count,
                            "NeedsReview closed a session it did not own"),
                        C5 = () => AtF07Matrix.ForeignFile(
                            path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"),
                    };
                }

                case "prefab.save_contents":
                {
                    var path = AtF07Matrix.P(key, "SaveMe.prefab");
                    string? original = null;
                    return new MatrixOp
                    {
                        Key = key,
                        OpId = "unity.prefab.save_contents", Seed = 'k',
                        Prep = () =>
                        {
                            _saveGuid = AtF07Matrix.CreatePrefab(path, "Atf07MSave");
                            original = File.ReadAllText(AtF07Matrix.Disk(path));
                            _saveContents = PrefabUtility.LoadPrefabContents(path);
                            var edit = new GameObject("Atf07MEdit");
                            edit.transform.SetParent(_saveContents.transform, false);
                        },
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["root"] = Cst(new RefResolver(
                                    PrefabContentsResources.FindEditingRoot)
                                .ToRef(_saveContents!, "unity.GameObjectRef")),
                        },
                        Outputs = Out(("prefab", "unity.PrefabAssetRef")),
                        Bind = runId => PrefabContentsOwnership.Bind(
                            RunResourceScope.Of(runId, 0), _saveGuid!, _saveContents!),
                        Applied = () => Assert.IsNull(
                            PrefabContentsResources.FindEditingRoot(_saveGuid!),
                            "save_contents did not close the session"),
                        // save_contents' §F5 row has no NotApplied — every
                        // non-Completed verdict is Unknown → NeedsReview.
                        C1 = MatrixVerdict.Review,
                        Unapply = () => AssetDatabase.DeleteAsset(path),
                        AfterC1 = () => Assert.IsNull(
                            AssetDatabase.LoadAssetAtPath<GameObject>(path),
                            "NeedsReview rewrote or recreated the prefab"),
                        C2 = MatrixVerdict.Adopted,
                        C3 = () => AtF07Matrix.ForeignFile(
                            path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"),
                        C4 = MatrixVerdict.Review,
                        C4Unapply = () =>
                        {
                            File.WriteAllText(AtF07Matrix.Disk(path), original!);
                            AssetDatabase.Refresh();
                        },
                        AfterC4 = () => Assert.AreEqual(
                            original, File.ReadAllText(AtF07Matrix.Disk(path)),
                            "NeedsReview rewrote the pre-existing content"),
                        C5 = () =>
                        {
                            AssetDatabase.DeleteAsset(path);
                            AtF07Matrix.CreatePrefab(path, "Atf07MForeign");
                        },
                    };
                }

                case "asset.create_material":
                {
                    var path = AtF07Matrix.P(key, "Mat.mat");
                    var shader = AtF07Matrix.P(key, "Probe.shader");
                    return new MatrixOp
                    {
                        Key = key,
                        OpId = CreateMaterialOperation.OperationId, Seed = 'm',
                        Prep = () => AtF07Matrix.WriteAssetFile(shader, ProbeShader()),
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["path"] = Cst("\"" + path + "\""),
                            ["shader"] = Cst("\"OpWeave/Atf07Probe\""),
                        },
                        Outputs = Out(("material", "unity.AssetRef<unity.Material>")),
                        Applied = () => Assert.IsNotNull(
                            AssetDatabase.LoadAssetAtPath<Material>(path),
                            "create_material did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => AssetDatabase.DeleteAsset(path),
                        C3 = () => AtF07Matrix.ForeignFile(
                            path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"),
                        C4Refused = true,
                        C4Prep = () =>
                        {
                            if (_case == 4)
                            {
                                AtF07Matrix.WriteAssetFile(shader, ProbeShader());
                                AtF07Matrix.WriteAssetFile(
                                    path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");
                            }
                        },
                        AfterC4 = () => Assert.IsTrue(
                            File.Exists(AtF07Matrix.Disk(path)),
                            "the refused Preflight removed the occupied artifact"),
                        C5 = () => AtF07Matrix.ForeignFile(
                            path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"),
                        AfterC5 = () => Assert.IsTrue(
                            File.Exists(AtF07Matrix.Disk(path)),
                            "NeedsReview removed the changed artifact"),
                    };
                }

                case "asset.create_so":
                {
                    var path = AtF07Matrix.P(key, "Obj.asset");
                    return new MatrixOp
                    {
                        Key = key,
                        OpId = CreateScriptableObjectOperation.OperationId, Seed = 'n',
                        Prep = () => AtF07Matrix.EnsureFolder(
                            Path.GetDirectoryName(path)!.Replace('\\', '/')),
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["path"] = Cst("\"" + path + "\""),
                            ["type"] = Cst("\"" +
                                typeof(ProbeFieldsAsset).FullName! + "\""),
                        },
                        Outputs = Out(("asset", "unity.AssetRef<unity.ScriptableObject>")),
                        Applied = () => Assert.IsNotNull(
                            AssetDatabase.LoadAssetAtPath<ProbeFieldsAsset>(path),
                            "create_scriptable_object did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => AssetDatabase.DeleteAsset(path),
                        C3 = () => AtF07Matrix.ForeignFile(
                            path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"),
                        C4Refused = true,
                        C4Prep = () =>
                        {
                            if (_case == 4)
                            {
                                AtF07Matrix.EnsureFolder(
                                    Path.GetDirectoryName(path)!.Replace('\\', '/'));
                                AtF07Matrix.WriteAssetFile(
                                    path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");
                            }
                        },
                        AfterC4 = () => Assert.IsTrue(
                            File.Exists(AtF07Matrix.Disk(path)),
                            "the refused Preflight removed the occupied artifact"),
                        C5 = () => AtF07Matrix.ForeignFile(
                            path, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"),
                    };
                }

                case "asset.move":
                {
                    var src = AtF07Matrix.P(key, "Src.txt");
                    var dst = AtF07Matrix.P(key, "Dst.txt");
                    string? guid = null;
                    return new MatrixOp
                    {
                        Key = key, OpId = MoveAssetOperation.OperationId, Seed = 'p',
                        Prep = () => guid = AtF07Matrix.CreateTextAsset(src, "atf07 move\n"),
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["asset"] = Cst(RunnerHostFixture.Json(
                                "{\"kind\":\"asset\",\"guid\":\"" + guid +
                                "\",\"path\":\"" + src + "\"}")),
                            ["destination"] = Cst("\"" + dst + "\""),
                        },
                        Outputs = Out(("asset", "unity.AssetRef<unity.Object>")),
                        Applied = () => Assert.IsFalse(
                            File.Exists(AtF07Matrix.Disk(src)),
                            "asset.move did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () =>
                        {
                            File.Move(AtF07Matrix.Disk(dst), AtF07Matrix.Disk(src));
                            if (File.Exists(AtF07Matrix.Disk(dst) + ".meta"))
                            {
                                File.Move(
                                    AtF07Matrix.Disk(dst) + ".meta",
                                    AtF07Matrix.Disk(src) + ".meta");
                            }
                            AssetDatabase.Refresh();
                        },
                        AfterC1 = () =>
                        {
                            Assert.IsTrue(File.Exists(AtF07Matrix.Disk(dst)),
                                "the re-executed move left nothing at destination");
                            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(dst),
                                "the moved asset lost its guid");
                        },
                        // The moved payload landed torn: the guid still
                        // resolves at the destination but the content-hash
                        // evidence no longer matches — Unknown.
                        C3 = () => AtF07Matrix.TornWrite(dst),
                        AfterC3 = () =>
                        {
                            Assert.IsTrue(
                                File.Exists(AtF07Matrix.Disk(dst)),
                                "NeedsReview removed the disturbed destination");
                            Assert.IsFalse(
                                File.Exists(AtF07Matrix.Disk(src)),
                                "NeedsReview revived the recorded source path");
                        },
                        C4Refused = true,
                        C4Prep = () =>
                        {
                            if (_case == 4)
                            {
                                AtF07Matrix.WriteAssetFile(dst, "occupied\n");
                            }
                        },
                        AfterC4 = () => Assert.AreEqual(
                            "occupied\n", File.ReadAllText(AtF07Matrix.Disk(dst)),
                            "the refused Preflight disturbed the occupied file"),
                        C5 = () => AtF07Matrix.ForeignFile(dst, "foreign\n"),
                        AfterC5 = () => Assert.AreEqual(
                            "foreign\n", File.ReadAllText(AtF07Matrix.Disk(dst)),
                            "NeedsReview overwrote the changed artifact"),
                    };
                }

                case "asset.copy":
                {
                    var src = AtF07Matrix.P(key, "CopySrc.txt");
                    var dst = AtF07Matrix.P(key, "CopyDst.txt");
                    string? guid = null;
                    return new MatrixOp
                    {
                        Key = key, OpId = CopyAssetOperation.OperationId, Seed = 'q',
                        Prep = () => guid = AtF07Matrix.CreateTextAsset(src, "atf07 copy\n"),
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["asset"] = Cst(RunnerHostFixture.Json(
                                "{\"kind\":\"asset\",\"guid\":\"" + guid +
                                "\",\"path\":\"" + src + "\"}")),
                            ["destination"] = Cst("\"" + dst + "\""),
                        },
                        Outputs = Out(("asset", "unity.AssetRef<unity.Object>")),
                        Applied = () => Assert.IsTrue(
                            File.Exists(AtF07Matrix.Disk(dst)),
                            "asset.copy did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => AssetDatabase.DeleteAsset(dst),
                        AfterC1 = () => Assert.IsTrue(
                            File.Exists(AtF07Matrix.Disk(dst)),
                            "the re-executed copy wrote nothing"),
                        C3 = () => AtF07Matrix.TornWrite(dst),
                        C4Refused = true,
                        C4Prep = () =>
                        {
                            if (_case == 4)
                            {
                                AtF07Matrix.WriteAssetFile(dst, "occupied\n");
                            }
                        },
                        AfterC4 = () => Assert.AreEqual(
                            "occupied\n", File.ReadAllText(AtF07Matrix.Disk(dst)),
                            "the refused Preflight disturbed the occupied file"),
                        C5 = () => AtF07Matrix.ForeignFile(dst, "foreign\n"),
                    };
                }

                case "asset.delete":
                {
                    var path = AtF07Matrix.P(key, "Del.txt");
                    var sibling = AtF07Matrix.P(key, "DelSibling.txt");
                    string? guid = null; string? bytes = null; string? meta = null;
                    return new MatrixOp
                    {
                        Key = key, OpId = DeleteAssetOperation.OperationId, Seed = 'r',
                        Prep = () =>
                        {
                            guid = AtF07Matrix.CreateTextAsset(path, "atf07 delete\n");
                            AtF07Matrix.CreateTextAsset(sibling, "sibling\n");
                            bytes = File.ReadAllText(AtF07Matrix.Disk(path));
                            meta = File.ReadAllText(AtF07Matrix.Disk(path) + ".meta");
                        },
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["asset"] = Cst(RunnerHostFixture.Json(
                                "{\"kind\":\"asset\",\"guid\":\"" + guid +
                                "\",\"path\":\"" + path + "\"}")),
                        },
                        Outputs = Out(),
                        Applied = () => Assert.IsFalse(
                            File.Exists(AtF07Matrix.Disk(path)),
                            "asset.delete did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () =>
                        {
                            File.WriteAllText(AtF07Matrix.Disk(path), bytes!);
                            File.WriteAllText(AtF07Matrix.Disk(path) + ".meta", meta!);
                            AssetDatabase.Refresh();
                        },
                        AfterC1 = () =>
                        {
                            Assert.IsFalse(File.Exists(AtF07Matrix.Disk(path)),
                                "the re-executed delete left the file behind");
                            Assert.IsTrue(File.Exists(AtF07Matrix.Disk(sibling)),
                                "the re-executed delete removed the sibling");
                        },
                        // The file is gone but its orphan .meta still
                        // occupies the path — a torn deletion.
                        C3 = () => File.WriteAllText(
                            AtF07Matrix.Disk(path) + ".meta", meta!),
                        AfterC3 = () => Assert.IsTrue(
                            File.Exists(AtF07Matrix.Disk(sibling)),
                            "NeedsReview disturbed the sibling"),
                        C4 = MatrixVerdict.Review,
                        C4Unapply = () => AtF07Matrix.ForeignFile(path, "foreign\n"),
                        AfterC4 = () => Assert.AreEqual(
                            "foreign\n", File.ReadAllText(AtF07Matrix.Disk(path)),
                            "NeedsReview deleted the foreign same-path file"),
                        // Same guid, changed content — the artifact another
                        // operation rewrote is not the recorded target.
                        C5 = () =>
                        {
                            File.WriteAllText(AtF07Matrix.Disk(path), "changed\n");
                            File.WriteAllText(AtF07Matrix.Disk(path) + ".meta", meta!);
                            AssetDatabase.Refresh();
                        },
                        AfterC5 = () => Assert.AreEqual(
                            "changed\n", File.ReadAllText(AtF07Matrix.Disk(path)),
                            "NeedsReview removed the changed artifact"),
                    };
                }

                case "script.template":
                {
                    const string cls = "Atf07MTemplate";
                    var path = AtF07Matrix.P(key, cls + ".cs");
                    return new MatrixOp
                    {
                        Key = key,
                        OpId = CreateScriptFromTemplateOperation.OperationId, Seed = 's',
                        Prep = () => AtF07Matrix.EnsureFolder(
                            Path.GetDirectoryName(path)!.Replace('\\', '/')),
                        Inputs = () => new Dictionary<string, InputSource>
                        {
                            ["template"] = Cst("\"mono_behaviour\""),
                            ["className"] = Cst("\"" + cls + "\""),
                            ["folder"] = Cst("\"" +
                                Path.GetDirectoryName(path)!.Replace('\\', '/') + "\""),
                        },
                        Outputs = Out(
                            ("script", "unity.AssetRef<unity.MonoScript>"),
                            ("typeName", "unity.TypeName")),
                        Applied = () => Assert.IsTrue(
                            File.Exists(AtF07Matrix.Disk(path)),
                            "script.create_from_template did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => AssetDatabase.DeleteAsset(path),
                        C3 = () =>
                        {
                            var full = File.ReadAllText(AtF07Matrix.Disk(path));
                            File.WriteAllText(
                                AtF07Matrix.Disk(path),
                                full.Substring(0, full.Length / 2));
                            AssetDatabase.Refresh();
                        },
                        C4Refused = true,
                        C4Prep = () =>
                        {
                            if (_case == 4)
                            {
                                AtF07Matrix.WriteAssetFile(
                                    path, "// occupied\nclass " + cls + " {}\n");
                            }
                        },
                        AfterC4 = () => Assert.IsTrue(
                            File.ReadAllText(AtF07Matrix.Disk(path))
                                .Contains("occupied"),
                            "the refused Preflight overwrote the occupied file"),
                        C5 = () => AtF07Matrix.ForeignFile(
                            path, "// foreign\nclass " + cls + " {}\n"),
                    };
                }

                case "script.create":
                {
                    var path = AtF07Matrix.P(key, "Atf07MCreated.cs");
                    var content = "public sealed class Atf07MCreated {}\n";
                    string? preBytes = null;
                    return new MatrixOp
                    {
                        Key = key, OpId = CreateScriptOperation.OperationId, Seed = 't',
                        Prep = () =>
                        {
                            AtF07Matrix.EnsureFolder(
                                Path.GetDirectoryName(path)!.Replace('\\', '/'));
                            if (_case == 4)
                            {
                                AtF07Matrix.WriteAssetFile(path, "// pre-existing\n");
                                preBytes = "// pre-existing\n";
                            }
                        },
                        Inputs = () =>
                        {
                            var inputs = new Dictionary<string, InputSource>
                            {
                                ["path"] = Cst("\"" + path + "\""),
                                ["content"] = Cst(JsonValue.From(new JValue(content))),
                            };
                            if (_case == 4)
                            {
                                inputs["overwrite"] = Cst("true");
                            }
                            return inputs;
                        },
                        Outputs = Out(("script", "unity.AssetRef<unity.MonoScript>")),
                        Applied = () => Assert.AreEqual(
                            content, File.ReadAllText(AtF07Matrix.Disk(path)),
                            "script.create did not run"),
                        C1 = MatrixVerdict.Reexec,
                        Unapply = () => AssetDatabase.DeleteAsset(path),
                        C3 = () => File.WriteAllText(
                            AtF07Matrix.Disk(path),
                            content.Substring(0, content.Length / 2)),
                        C4 = MatrixVerdict.Reexec,
                        C4Unapply = () => AtF07Matrix.ForeignFile(path, preBytes!),
                        AfterC4 = () => Assert.AreEqual(
                            content, File.ReadAllText(AtF07Matrix.Disk(path)),
                            "the re-executed overwrite did not rewrite the file"),
                        C5 = () => AtF07Matrix.ForeignFile(path, "// foreign\n"),
                    };
                }

                case "package.add":
                {
                    _git = AtF07Matrix.EnsureGitFixture();
                    return new MatrixOp
                    {
                        Key = key,
                        OpId = UnityPackageAddOperation.OperationId, Seed = 'u',
                        Inputs = () => new Dictionary<string, InputSource>
                            { ["id"] = Cst(JsonValue.From(new JValue(Dep))) },
                        Outputs = Out(("package", "core.Json")),
                        C1 = MatrixVerdict.Reexec,
                        AfterC1 = () => Assert.GreaterOrEqual(
                            RunnerHostFixture.JournalCount(
                                AtF07Matrix.StoreRootFor(key + "-case" + _case),
                                _runId!,
                                "\"type\":\"step_waiting\""),
                            2,
                            "the NotApplied package.add never re-issued its request"),
                        C2 = MatrixVerdict.Adopted,
                        // A same-name install the step never made — the
                        // pre-state already records it, so Completed can
                        // never claim it (RecordedMatch → PreExisting).
                        C4 = MatrixVerdict.Review,
                        C4AddView = () => GitView(_git.tagged),
                    };
                }

                case "package.remove":
                {
                    _git = AtF07Matrix.EnsureGitFixture();
                    return new MatrixOp
                    {
                        Key = key,
                        OpId = UnityPackageRemoveOperation.OperationId, Seed = 'v',
                        Inputs = () => new Dictionary<string, InputSource>
                            { ["name"] = Cst("\"" + AtF07Matrix.PkgName + "\"") },
                        Outputs = Out(),
                        C1 = MatrixVerdict.Reexec,
                        AfterC1 = () => Assert.GreaterOrEqual(
                            RunnerHostFixture.JournalCount(
                                AtF07Matrix.StoreRootFor(key + "-case" + _case),
                                _runId!,
                                "\"type\":\"step_waiting\""),
                            2,
                            "the NotApplied package.remove never re-issued"),
                        C2 = MatrixVerdict.Adopted,
                        // A same-name package whose provenance is not the
                        // recorded Git install stands where the evidence
                        // row points — Changed, never Completed.
                        C4 = MatrixVerdict.Review,
                    };
                }
            }
            throw new ArgumentOutOfRangeException(nameof(key), key);
        }
    }
}
