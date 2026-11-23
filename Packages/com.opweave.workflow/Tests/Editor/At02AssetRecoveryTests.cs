#nullable enable

// T-W4.5-03 [Edit] tests: local:T-W4.5-03:at02-asset — the Runner half of
// the SPEC §F5 contract for the five Reconcilable Operations of the §I.3
// Asset group (unity.asset.create_material,
// unity.asset.create_scriptable_object, unity.asset.move, unity.asset.copy,
// unity.asset.delete): a Run parked mid-flight and cut back to the bare
// step_started{step:0} — its Execute effect applied, its recorded evidence
// then disturbed — must be decided Unknown and end NeedsReview on the §F4
// recovery pass, never an automatic success and never an automatic
// re-execution (SPEC §F5 rules 1-6, §F0). One more per-Operation row
// completes the §F5 six: an Editor restart the journaled snapshot
// outlives — every §I.3 row's evidence is file identity, which a session
// boundary cannot lose, so the restarted verdict is Completed and the
// resumed Run adopts the step's outputs, never re-runs. The group's
// Waitable, unity.asset.import, gets the waiting-step half: a Run parked
// at its step_waiting with the persisted resume token resumes the wait on
// recovery and settles through Poll, never re-executing the import.
// The same-domain tests drive the real OpWeaveRunnerHost + FileRunStore
// path the T-W4.3-03 fixtures established; the [UnityTest] companion runs
// the identical contract across a real domain reload (SPEC §T.3), where
// every started-only disturbance in the group must still end NeedsReview
// one domain later.
//
// The journal surgery and the park/recover mechanics mirror
// At02PrefabRecoveryTests: [step -> hold] plans on the real Operations,
// TruncateThrough to the started-only tail, and the marker-script +
// domain-sentinel reload of the reload companion. SPEC §E2 admits at
// most one mutating Run per Run Store, so the reload companion parks each
// Operation's Run in a store of its own and never disposes a phase-1
// host: the OS exclusion each parked Run holds is a process-held handle
// that survives the domain reload, the hand-off seam of SPEC §E2 item 3
// the post-reload pass re-adopts.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.5-03:at02-asset</c> recovery cases: every Reconcilable
    /// Operation of the SPEC §I.3 Asset group, disturbed so its §F5
    /// evidence row no longer holds, ends NeedsReview — in the same domain
    /// and across a real domain reload.
    /// </summary>
    internal sealed class At02AssetRecoveryTests
    {
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveAt02AssetRecovery");

        private static string StoreRoot => Path.Combine(ProbeDir, "run-root");
        private static string StateFile => Path.Combine(ProbeDir, "state.txt");

        // The reload group's per-Operation Run Store: five started-only
        // Runs unfinished at once need a store each — SPEC §E2 admits at
        // most one mutating Run per store, so a second accept on one
        // coordinator fails OW-RUN-002, and the §F4 recovery pass drives
        // only the single Run the exclusion passes to.
        private static string StoreRootFor(string key) =>
            Path.Combine(ProbeDir, "run-root-" + key);

        // The group stores in phase order — WriteState persists the run
        // ids in this order and the post-reload phase walks them the
        // same way.
        private static readonly string[] s_groupKeys =
            { "material", "object", "move", "copy", "delete" };

        private static IEnumerable<string> AllStoreRoots()
        {
            yield return StoreRoot;
            for (var i = 0; i < s_groupKeys.Length; i++)
            {
                yield return StoreRootFor(s_groupKeys[i]);
            }
        }

        private const string ProbeFolder = "Assets/OpWeaveAt02AssetResume";
        private const string BaseScenePath = ProbeFolder + "/Base.unity";
        private const string MaterialPath = ProbeFolder + "/At02Material.mat";
        private const string MaterialResPath = ProbeFolder + "/At02MaterialRes.mat";
        private const string ObjectPath = ProbeFolder + "/At02Object.asset";
        private const string ObjectResPath = ProbeFolder + "/At02ObjectRes.asset";
        private const string MoveSourcePath = ProbeFolder + "/At02Move.txt";
        private const string MoveDestinationPath = ProbeFolder + "/At02Moved.txt";
        private const string MoveResSourcePath = ProbeFolder + "/At02MoveRes.txt";
        private const string MoveResDestinationPath = ProbeFolder + "/At02MovedRes.txt";
        private const string CopySourcePath = ProbeFolder + "/At02CopySrc.txt";
        private const string CopyDestinationPath = ProbeFolder + "/At02CopyDst.txt";
        private const string CopyResSourcePath = ProbeFolder + "/At02CopyResSrc.txt";
        private const string CopyResDestinationPath = ProbeFolder + "/At02CopyResDst.txt";
        private const string DeletePath = ProbeFolder + "/At02Delete.txt";
        private const string DeleteResPath = ProbeFolder + "/At02DeleteRes.txt";
        private const string ImportWaitedPath = ProbeFolder + "/At02ImportWaited.txt";

        private static string MarkerFolder => "Assets/Editor/OpWeaveAt02AssetReload";
        private static string MarkerDisk =>
            Path.Combine(
                ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveAt02AssetReload\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        // A real Shader asset declaring the §G7 item 4 property kinds —
        // the AssetCreateOperationTests probe the positive material run
        // resolves by name.
        private const string ProbeShaderPath = ProbeFolder + "/Probe.shader";
        private const string ProbeShaderName = "OpWeave/AssetCreateProbe";
        private const string ProbeShaderSource =
            "Shader \"OpWeave/AssetCreateProbe\"\n" +
            "{\n" +
            "    Properties\n" +
            "    {\n" +
            "        _Glossiness (\"Smoothness\", Range(0, 1)) = 0.5\n" +
            "        _Metallic (\"Metallic\", Range(0, 1)) = 0\n" +
            "        _Color (\"Color\", Color) = (1, 1, 1, 1)\n" +
            "        _EmissionColor (\"Emission\", Color) = (0, 0, 0, 0)\n" +
            "        _Offset (\"Offset\", Vector) = (0, 0, 0, 0)\n" +
            "        _MainTex (\"Main Tex\", 2D) = \"white\" {}\n" +
            "    }\n" +
            "    SubShader\n" +
            "    {\n" +
            "        Pass\n" +
            "        {\n" +
            "            CGPROGRAM\n" +
            "            #pragma vertex vert\n" +
            "            #pragma fragment frag\n" +
            "            float4 vert(float4 v : POSITION) : SV_Position { return v; }\n" +
            "            fixed4 frag() : SV_Target { return fixed4(1, 1, 1, 1); }\n" +
            "            ENDCG\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        // The content a disturbed artifact carries — text no honest
        // evidence row can match, kept different from every fixture write
        // so the disturbance is never mistaken for the step's own output.
        private const string DisturbedContent = "disturbed by at-02\n";

        private const string WorkflowId = "example.at02.asset.recovery";
        private const string HoldOp = "test.at02.asset.resume.hold";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private static void Crumb(string message) => Debug.Log("[W4503] " + message);

        private readonly List<RunnerHostFixture> _fixtures =
            new List<RunnerHostFixture>();

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static PhaseResult Pass() => new PhaseResult { Ok = true };

            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        // ── Setup / cleanup ─────────────────────────────────────────────

        // No plain [SetUp]/[TearDown]: NUnit's setup/teardown command keeps
        // no persisted state, so a [UnityTest] resumed across a domain
        // reload runs [SetUp] again before the iterator continues — here
        // that deleted ProbeDir and the phase-1 state file mid-test. The
        // cleanup lives in [UnitySetUp]/[UnityTearDown], the persisted
        // outer command the runner executes exactly once per test for
        // [Test] and [UnityTest] alike — the ResumeBranchingReloadTests
        // structure.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            NewUntitledScene();
            if (CleanupArtifacts())
            {
                yield return new RecompileScripts(false);
            }
            NewBaseScene();
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            DisposeAll();
            NewUntitledScene();
            ClearHandoffRecords();
            if (CleanupArtifacts())
            {
                yield return new RecompileScripts(false);
            }
        }

        private void DisposeAll()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            _fixtures.Clear();
        }

        // A saved, clean base scene: the harness worlds and the Runner's
        // scene checks need no scene content, only a clean loaded scene.
        private static void NewBaseScene()
        {
            EnsureFolder(ProbeFolder);
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(
                EditorSceneManager.SaveScene(scene, BaseScenePath), Is.True,
                "the recovery test's base scene must save.");
        }

        private static void NewUntitledScene() =>
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // The T-W4.3-01 cleanup contract, verbatim in shape, once per Run
        // Store the fixture touches: the exclusion this process still
        // holds on a probe root — the previous domain's or a fixture's —
        // is taken through the hand-off seam and ended the orderly way so
        // lock.json empties BEFORE the handle frees; then both hand-off
        // records drop.
        private static void ReleaseLeakedState()
        {
            foreach (var storeRoot in AllStoreRoots())
            {
                ReleaseLeakedState(storeRoot);
            }
        }

        private static void ReleaseLeakedState(string storeRoot)
        {
            var obtained = RunLockHandoff.TryObtain(storeRoot);
            if (!obtained.Ok || obtained.Value is not { } held)
            {
                Crumb("the probe Run Store is excluded elsewhere: " +
                    Describe(obtained.Diagnostics));
                RunLockHandoff.Clear(storeRoot);
                return;
            }
            if (!held.Release().Ok)
            {
                held.Dispose();
            }
            RunLockHandoff.Clear(storeRoot);
        }

        private static void ClearHandoffRecords()
        {
            foreach (var storeRoot in AllStoreRoots())
            {
                RunLockHandoff.Clear(storeRoot);
            }
        }

        private static bool CleanupArtifacts()
        {
            ReleaseLeakedState();
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            var dirty = false;
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
                dirty = true;
            }
            var probeDisk = PhysicalPath(ProbeFolder);
            if (Directory.Exists(probeDisk))
            {
                try { Directory.Delete(probeDisk, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
                dirty = true;
            }
            if (File.Exists(probeDisk + ".meta"))
            {
                try { File.Delete(probeDisk + ".meta"); } catch (Exception) { }
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
            if (File.Exists(MarkerDisk + ".meta"))
            {
                File.Delete(MarkerDisk + ".meta");
                dirty = true;
            }
            return dirty;
        }

        // ── Fixtures, plans, accept/tick/recover ────────────────────────

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(StoreRoot);
            _fixtures.Add(fixture);
            RegisterAll(fixture);
            return fixture;
        }

        private void DisposeFixture(RunnerHostFixture fixture)
        {
            fixture.Dispose();
            _fixtures.Remove(fixture);
        }

        // Every Reconcilable Operation of the group, the Waitable import
        // and the hold step, at the seeds the parked plans' fingerprints
        // were built with — a fresh host must resolve each step's
        // implementation after a host swap or a domain reload. The
        // allowed-paths seams bind the host's own ActiveRunAllowedPaths —
        // the frozen settings snapshot of the Run the accept produced —
        // never the test's.
        private static void RegisterAll(RunnerHostFixture fixture)
        {
            var host = fixture.Host;
            RegisterAs(
                fixture, new CreateMaterialOperation(), 'a',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new CreateScriptableObjectOperation(), 'c',
                OperationOrigin.Standard);
            RegisterAs(
                fixture,
                new MoveAssetOperation(() => host.ActiveRunAllowedPaths), 'e',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new CopyAssetOperation(), '0',
                OperationOrigin.Standard);
            RegisterAs(
                fixture,
                new DeleteAssetOperation(() => host.ActiveRunAllowedPaths), '2',
                OperationOrigin.Standard);
            RegisterAs(
                fixture, new ImportAssetOperation(), '6',
                OperationOrigin.Standard);
            fixture.Register(
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), '4');
            fixture.Probe.Know(typeof(ProbeFieldsAsset).FullName!);
        }

        private static void RegisterAs(
            RunnerHostFixture fixture, IOperation op, char hashSeed, OperationOrigin origin) =>
            fixture.Operations.Add(new RegisteredOperation(
                op.Descriptor,
                origin,
                "Example.Operations",
                null,
                new string(hashSeed, 64),
                new string((char)(hashSeed + 1), 64))
            {
                Implementation = op,
            });

        private static PlanStep Step(
            int index,
            string nodeId,
            string operationId,
            char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            int next = -1,
            Idempotency idempotency = Idempotency.Unsafe) =>
            RunnerHostFixture.Step(
                index, nodeId, operationId, hashSeed,
                inputs: inputs, outputTypes: outputTypes,
                next: next, idempotency: idempotency);

        private static WorkflowDocument Doc(
            IReadOnlyList<(string id, string operation)> nodes,
            params (string from, string to)[] edges) =>
            RunnerHostFixture.Doc(
                WorkflowId,
                nodes
                    .Select(n => new NodeData { Id = n.id, Operation = n.operation, Version = 1 })
                    .ToList(),
                edges
                    .Select(e => new ControlEdge(e.from, e.to))
                    .ToList());

        private static string Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, new AcceptOptions(null, false));
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        private static void Tick(RunnerHostFixture fixture)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "the tick failed: " + Describe(tick.Diagnostics));
        }

        private static RunRecoveryReport Recover(RunnerHostFixture fixture)
        {
            var recovered = fixture.Host.RecoverRuns();
            Assert.IsTrue(recovered.Ok, "the recovery pass failed: " +
                Describe(recovered.Diagnostics));
            Assert.IsNotNull(recovered.Value);
            return recovered.Value!;
        }

        private static void AssertNeedsReview(
            string storeRoot, RunnerHostFixture fixture, string runId, string because)
        {
            Assert.AreEqual(
                RunState.NeedsReview,
                fixture.Host.Coordinator.StateOf(runId),
                because + " must end in NeedsReview; journal: " + string.Join(
                    " | ", RunnerHostFixture.JournalLines(storeRoot, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(
                    storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a review-needed resume never journals run_resumed");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    storeRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                because + ": the Unknown verdict is journaled step_needs_review (§E1)");
        }

        // The decided half of an §F5 started-only judgement: the verdict —
        // whichever the row's evidence proved — journaled run_resumed
        // (§F4 item 6's write-ahead) and never step_needs_review; the
        // caller's checks then name whether the resume adopted the step's
        // outputs or re-executed it.
        private static void AssertResumed(
            string storeRoot, RunnerHostFixture fixture, string runId, string because)
        {
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(
                    storeRoot, runId, "\"type\":\"run_resumed\""),
                because + ": a decided resume journals run_resumed before " +
                    "the outcome is applied (§F4 item 6)");
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    storeRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                because + " must not end the interrupted step in NeedsReview");
            Assert.AreNotEqual(
                RunState.NeedsReview,
                fixture.Host.Coordinator.StateOf(runId),
                because + " must not end in NeedsReview");
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // ── Journal surgery ─────────────────────────────────────────────

        private static string[] Lines(string storeRoot, string runId) =>
            RunnerHostFixture.JournalLines(storeRoot, runId);

        private static void WriteLines(
            string storeRoot, string runId, IEnumerable<string> lines)
        {
            File.WriteAllText(
                RunnerHostFixture.JournalPath(storeRoot, runId),
                string.Join("\n", lines) + "\n", s_utf8);
        }

        // Keeps the records through the FIRST line carrying both fragments —
        // the crash window between that durable record and whatever the loop
        // was about to append next.
        private static void TruncateThrough(
            string storeRoot, string runId, string typeFragment, string stepFragment)
        {
            var lines = Lines(storeRoot, runId);
            var cut = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(typeFragment) && lines[i].Contains(stepFragment))
                {
                    cut = i;
                    break;
                }
            }
            Assert.GreaterOrEqual(cut, 0,
                "the journal never recorded " + typeFragment + " " + stepFragment +
                ": " + string.Join(" | ", lines));
            WriteLines(storeRoot, runId, lines.Take(cut + 1));
        }

        // The regression guard of the started-only fixtures: the journal the
        // recovering host reads must END at the bare step_started{step} — a
        // record past it is a two-steps-in-flight journal the §F4 rebuild
        // correctly refuses, never the interruption the fixture means to
        // leave.
        private static void AssertStartedOnlyTail(string storeRoot, string runId, int step)
        {
            var tail = Lines(storeRoot, runId);
            Assert.IsTrue(
                tail.Length > 0 &&
                tail[tail.Length - 1].Contains("\"type\":\"step_started\"") &&
                tail[tail.Length - 1].Contains("\"step\":" + step),
                "the journal must end at step " + step +
                "'s step_started with no later records; journal: " +
                string.Join(" | ", tail));
        }

        // The Editor-restart half of the §F5 started-only rows: the evidence
        // the step journaled was recorded in an Editor session that no longer
        // exists. One Editor cannot be restarted, so the journaled
        // step_started{step:0} snapshot's recorded editorSessionId is moved
        // to a session this Editor never had — re-encoded through
        // JournalCodec so seq, the required flags and the crc stay a valid
        // v1 envelope — which is exactly the envelope the first recovery
        // pass of a new Editor session would read from this journal.
        private static void SimulateEditorRestart(string storeRoot, string runId)
        {
            var lines = Lines(storeRoot, runId);
            var rewritten = false;
            for (var i = 1; i < lines.Length; i++)
            {
                var read = JournalCodec.ReadRecord(lines[i], i);
                if (read.Status != JournalLineStatus.Decoded ||
                    read.Record is not { } record ||
                    record.Type != JournalRecordType.StepStarted ||
                    record.Step != 0 ||
                    record.Data.ToToken() is not JObject data ||
                    data["snapshot"] is not JObject ||
                    data["snapshot"]!["editorSessionId"] is null)
                {
                    continue;
                }
                data = (JObject)data.DeepClone();
                ((JObject)data["snapshot"]!)["editorSessionId"] =
                    "at02-simulated-editor-restart";
                lines[i] = JournalCodec.WriteRecord(
                    record with { Data = JsonValue.From(data) });
                rewritten = true;
            }
            Assert.IsTrue(
                rewritten,
                "no step_started{step:0} snapshot carried an editorSessionId " +
                "the restart could invalidate — the restart case exercises " +
                "nothing");
            WriteLines(storeRoot, runId, lines);
        }

        private static void WriteState(params string[] lines)
        {
            Directory.CreateDirectory(ProbeDir);
            File.WriteAllText(StateFile, string.Join("\n", lines) + "\n", s_utf8);
        }

        private static string[] ReadState()
        {
            Assert.IsTrue(File.Exists(StateFile), "the phase-1 state file is missing");
            return File.ReadAllLines(StateFile);
        }

        private static void ArmReload()
        {
            Directory.CreateDirectory(MarkerDisk);
            File.WriteAllText(
                Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
            AssetDatabase.Refresh();
            ArmDomainSentinel();
        }

        // ── Asset fixtures ──────────────────────────────────────────────

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(
                ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

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

        // A real imported TextAsset — returns its guid for the §B5
        // AssetRef the plan constants carry.
        private static string CreateTextAsset(string path, string contents)
        {
            File.WriteAllText(PhysicalPath(path), contents);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            Assert.That(guid, Is.Not.Empty, "the fixture asset must import: " + path);
            return guid;
        }

        // The §B5 AssetRef document for a recorded guid/path pair —
        // Reconcile contexts must not re-resolve assets the step already
        // moved or deleted.
        private static string AssetRefJson(string guid, string path) =>
            "{\"kind\":\"asset\",\"guid\":\"" + guid + "\",\"path\":\"" + path + "\"}";

        // A real Shader asset declaring the §G7 item 4 property kinds —
        // the AssetCreateOperationTests probe.
        private static string CreateProbeShader()
        {
            File.WriteAllText(PhysicalPath(ProbeShaderPath), ProbeShaderSource);
            AssetDatabase.ImportAsset(
                ProbeShaderPath, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(Shader.Find(ProbeShaderName), Is.Not.Null,
                "the probe shader must resolve by name: " + ProbeShaderName);
            return ProbeShaderName;
        }

        // The disturbed-artifact write the file-producing Operations get:
        // foreign bytes over the path Execute wrote — content no recorded
        // hash or normalized serialized form can match, so the §F5 check
        // settles Unknown instead of the Completed a stale evidence row
        // would have produced. Only ever applied to .txt destinations: a
        // text file imports whatever its bytes hold.
        private static void DamageAssetFile(string assetPath)
        {
            File.WriteAllText(PhysicalPath(assetPath), DisturbedContent);
            AssetDatabase.Refresh();
        }

        // The disturbed-artifact write a .mat gets: a different valid
        // Material over the path Execute wrote — the AssetCreateOperation
        // foreign-fixture shape — so the recorded serialized content can
        // never match and the file stays importable.
        private static void DamageMaterial(string assetPath)
        {
            var material = new Material(Shader.Find("Standard"));
            AssetDatabase.CreateAsset(material, assetPath);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<Material>(assetPath),
                "the disturbance could not overwrite " + assetPath);
        }

        // The disturbed-artifact write a .asset gets: a different valid
        // ScriptableObject type over the path Execute wrote — the
        // recorded CLR type can never match a ProbeAsset instance.
        private static void DamageObject(string assetPath)
        {
            var foreign = ScriptableObject.CreateInstance<ProbeAsset>();
            AssetDatabase.CreateAsset(foreign, assetPath);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath),
                "the disturbance could not overwrite " + assetPath);
        }

        // The disturbance a delete row gets: a foreign artifact back at
        // the freed path — Occupied while the recorded guid is gone is
        // Unknown, never the absent-artifact Completed. A text file keeps
        // the re-occupation importable.
        private static void ReoccupyPath(string assetPath)
        {
            File.WriteAllText(PhysicalPath(assetPath), DisturbedContent);
            AssetDatabase.Refresh();
        }

        // ── The [op -> hold] park ───────────────────────────────────────

        // Accepts [step -> hold] and ticks once: step 0 executes, step 1's
        // waitable parks the Run mid-flight — the interruption the journal
        // then fakes by truncation back to the bare step_started{step:0}.
        private static string ParkStartedOnly(
            RunnerHostFixture fixture,
            string opId,
            char hashSeed,
            IReadOnlyDictionary<string, InputSource> inputs,
            IReadOnlyDictionary<string, string>? outputTypes = null)
        {
            var steps = new[]
            {
                Step(0, "s", opId, hashSeed, next: 1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: inputs, outputTypes: outputTypes),
                Step(1, "h", HoldOp, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(new[] { ("s", opId), ("h", HoldOp) }, ("s", "h"));
            var runId = Accept(fixture, plan, doc);
            Tick(fixture);
            Assert.AreEqual(
                RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                "the " + opId + " Run did not park mid-flight; journal: " +
                string.Join(" | ", Lines(fixture.Root, runId)));
            return runId;
        }

        // unity.asset.import's own park: the Waitable Execute returns
        // Waiting on the first tick, so the Run's journal carries the live
        // step_waiting{step:0} a suspended Run would — the record a
        // recovery pass resumes the wait from, never a truncated one.
        private static string ParkImportWaiting(
            RunnerHostFixture fixture,
            IReadOnlyDictionary<string, InputSource> inputs)
        {
            var steps = new[]
            {
                Step(0, "s", ImportAssetOperation.OperationId, '6', next: 1,
                    idempotency: Idempotency.Idempotent,
                    inputs: inputs, outputTypes: s_assetOut),
                Step(1, "h", HoldOp, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(
                new[] { ("s", ImportAssetOperation.OperationId), ("h", HoldOp) },
                ("s", "h"));
            var runId = Accept(fixture, plan, doc);
            Tick(fixture);
            Assert.AreEqual(
                RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                "the unity.asset.import Run did not park at its wait; " +
                "journal: " + string.Join(" | ", Lines(fixture.Root, runId)));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    fixture.Root, runId, "\"type\":\"step_waiting\"", "\"step\":0"),
                "the import Run must journal step_waiting{step:0} with the " +
                "persisted resume token");
            return runId;
        }

        // ── Plan inputs ─────────────────────────────────────────────────

        private static readonly IReadOnlyDictionary<string, string> s_materialOut =
            new Dictionary<string, string>
            {
                ["material"] = "unity.AssetRef<unity.Material>",
            };

        private static readonly IReadOnlyDictionary<string, string> s_objectOut =
            new Dictionary<string, string>
            {
                ["asset"] = "unity.AssetRef<unity.ScriptableObject>",
            };

        private static readonly IReadOnlyDictionary<string, string> s_assetOut =
            new Dictionary<string, string>
            {
                ["asset"] = "unity.AssetRef<unity.Object>",
            };

        private static Dictionary<string, InputSource> MaterialInputs(string path) =>
            new Dictionary<string, InputSource>
            {
                ["path"] = new Constant(RunnerHostFixture.Json("\"" + path + "\"")),
                ["shader"] = new Constant(
                    RunnerHostFixture.Json("\"" + ProbeShaderName + "\"")),
            };

        private static Dictionary<string, InputSource> ObjectInputs(string path) =>
            new Dictionary<string, InputSource>
            {
                ["path"] = new Constant(RunnerHostFixture.Json("\"" + path + "\"")),
                ["type"] = new Constant(RunnerHostFixture.Json(
                    "\"" + typeof(ProbeFieldsAsset).FullName! + "\"")),
            };

        private static Dictionary<string, InputSource> MoveInputs(
            string guid, string source, string destination) =>
            new Dictionary<string, InputSource>
            {
                ["asset"] = new Constant(
                    RunnerHostFixture.Json(AssetRefJson(guid, source))),
                ["destination"] = new Constant(
                    RunnerHostFixture.Json("\"" + destination + "\"")),
            };

        private static Dictionary<string, InputSource> DeleteInputs(
            string guid, string path) =>
            new Dictionary<string, InputSource>
            {
                ["asset"] = new Constant(
                    RunnerHostFixture.Json(AssetRefJson(guid, path))),
            };

        private static Dictionary<string, InputSource> ImportInputs(string path) =>
            new Dictionary<string, InputSource>
            {
                ["path"] = new Constant(RunnerHostFixture.Json("\"" + path + "\"")),
            };

        // ── SPEC §F5 across a host swap: every Reconcilable of the group ─

        // unity.asset.create_material: the Run wrote the .mat — then
        // foreign bytes stand at the recorded path, the one thing the
        // expected serialized content evidence cannot match.
        [Test]
        public void CreateMaterial_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            CreateProbeShader();
            var runId = ParkStartedOnly(
                fixture1, CreateMaterialOperation.OperationId, 'a',
                MaterialInputs(MaterialPath), s_materialOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<Material>(MaterialPath),
                "the first Execute did not write the Material");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            DamageMaterial(MaterialPath);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed asset.create_material evidence row");
            Assert.AreEqual(
                "Standard",
                AssetDatabase.LoadAssetAtPath<Material>(MaterialPath).shader.name,
                "NeedsReview neither rewrites the artifact nor re-executes — " +
                "the foreign Material stays");
        }

        // unity.asset.create_scriptable_object: same shape — the foreign
        // bytes at the recorded .asset path can never import as the
        // recorded CLR type, so Unknown is the only honest verdict.
        [Test]
        public void CreateScriptableObject_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var runId = ParkStartedOnly(
                fixture1, CreateScriptableObjectOperation.OperationId, 'c',
                ObjectInputs(ObjectPath), s_objectOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<ScriptableObject>(ObjectPath),
                "the first Execute did not write the asset");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            DamageObject(ObjectPath);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed asset.create_scriptable_object evidence row");
            Assert.AreEqual(
                typeof(ProbeAsset),
                AssetDatabase.LoadAssetAtPath<ScriptableObject>(ObjectPath)
                    .GetType(),
                "NeedsReview neither rewrites the artifact nor re-executes — " +
                "the foreign ScriptableObject stays");
        }

        // unity.asset.move: the artifact is at the destination the Run
        // moved it to — foreign bytes there fail the recorded content
        // hash, and the source staying free is what keeps the disturbance
        // attributable.
        [Test]
        public void Move_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var guid = CreateTextAsset(MoveSourcePath, "at-02 move fixture\n");
            var runId = ParkStartedOnly(
                fixture1, MoveAssetOperation.OperationId, 'e',
                MoveInputs(guid, MoveSourcePath, MoveDestinationPath), s_assetOut);
            Assert.IsFalse(
                File.Exists(PhysicalPath(MoveSourcePath)),
                "the first Execute did not move the source away");
            Assert.IsTrue(
                File.Exists(PhysicalPath(MoveDestinationPath)),
                "the first Execute did not write the destination");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            DamageAssetFile(MoveDestinationPath);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed asset.move evidence row");
            Assert.AreEqual(
                DisturbedContent,
                File.ReadAllText(PhysicalPath(MoveDestinationPath)),
                "NeedsReview leaves the foreign destination — a re-execution " +
                "would have moved the artifact back");
            Assert.IsFalse(
                File.Exists(PhysicalPath(MoveSourcePath)),
                "NeedsReview never re-executes — the source stays free");
        }

        // unity.asset.copy: the destination the Run wrote carries foreign
        // bytes — the recorded content hash fails, and the source the step
        // only read stays exactly as it was.
        [Test]
        public void Copy_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var guid = CreateTextAsset(CopySourcePath, "at-02 copy fixture\n");
            var runId = ParkStartedOnly(
                fixture1, CopyAssetOperation.OperationId, '0',
                MoveInputs(guid, CopySourcePath, CopyDestinationPath), s_assetOut);
            Assert.IsTrue(
                File.Exists(PhysicalPath(CopyDestinationPath)),
                "the first Execute did not write the copy");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            DamageAssetFile(CopyDestinationPath);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed asset.copy evidence row");
            Assert.AreEqual(
                DisturbedContent,
                File.ReadAllText(PhysicalPath(CopyDestinationPath)),
                "NeedsReview leaves the foreign copy — a re-execution would " +
                "have failed on the occupied destination");
            Assert.AreEqual(
                "at-02 copy fixture\n",
                File.ReadAllText(PhysicalPath(CopySourcePath)),
                "the read-only source stays untouched");
        }

        // unity.asset.delete: the path the Run freed is re-occupied by a
        // foreign file — an artifact at the recorded path while its guid
        // is unresolvable is Unknown, never the absent-artifact Completed.
        [Test]
        public void Delete_StartedOnlyDisturbedBecomesNeedsReview()
        {
            var fixture1 = NewFixture();
            var guid = CreateTextAsset(DeletePath, "at-02 delete fixture\n");
            var runId = ParkStartedOnly(
                fixture1, DeleteAssetOperation.OperationId, '2',
                DeleteInputs(guid, DeletePath));
            Assert.IsFalse(
                File.Exists(PhysicalPath(DeletePath)),
                "the first Execute did not trash the target");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            ReoccupyPath(DeletePath);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertNeedsReview(
                StoreRoot, fixture2, runId,
                "a disturbed asset.delete evidence row");
            Assert.AreEqual(
                DisturbedContent, File.ReadAllText(PhysicalPath(DeletePath)),
                "NeedsReview leaves the foreign file — a re-execution would " +
                "have deleted an artifact the step never wrote");
        }

        // ── The Editor-restart rows: file identity survives the session ─

        // unity.asset.create_material: the row's evidence is the artifact's
        // normalized serialized content — a session boundary cannot lose
        // it — so the restarted-session verdict is Completed and the
        // resumed Run adopts the step's outputs.
        [Test]
        public void CreateMaterial_RestartedSessionResumes()
        {
            var fixture1 = NewFixture();
            CreateProbeShader();
            var runId = ParkStartedOnly(
                fixture1, CreateMaterialOperation.OperationId, 'a',
                MaterialInputs(MaterialResPath), s_materialOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<Material>(MaterialResPath),
                "the first Execute did not write the Material");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a asset.create_material whose snapshot outlived its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the serialized-content evidence survives the session " +
                "boundary");
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialResPath);
            Assert.IsNotNull(material, "the artifact is still importable");
            Assert.AreEqual(
                ProbeShaderName, material!.shader.name,
                "the artifact is exactly what the step saved — nothing re-ran");
        }

        // unity.asset.create_scriptable_object: the recorded CLR type and
        // expected serialized content are file identity — the restarted
        // verdict is Completed and the outputs are adopted.
        [Test]
        public void CreateScriptableObject_RestartedSessionResumes()
        {
            var fixture1 = NewFixture();
            var runId = ParkStartedOnly(
                fixture1, CreateScriptableObjectOperation.OperationId, 'c',
                ObjectInputs(ObjectResPath), s_objectOut);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<ScriptableObject>(ObjectResPath),
                "the first Execute did not write the asset");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a asset.create_scriptable_object whose snapshot outlived " +
                "its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the file-identity evidence survives the session boundary");
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(ObjectResPath);
            Assert.IsNotNull(asset, "the artifact is still importable");
            Assert.AreEqual(
                typeof(ProbeFieldsAsset), asset!.GetType(),
                "the artifact is exactly what the step saved — nothing re-ran");
        }

        // unity.asset.move: the destination's recorded guid and content
        // hash re-identify the moved artifact across the restart — the
        // verdict is Completed and the adopted output names the artifact
        // at its moved path.
        [Test]
        public void Move_RestartedSessionResumes()
        {
            var fixture1 = NewFixture();
            var guid = CreateTextAsset(MoveResSourcePath, "at-02 move fixture\n");
            var runId = ParkStartedOnly(
                fixture1, MoveAssetOperation.OperationId, 'e',
                MoveInputs(guid, MoveResSourcePath, MoveResDestinationPath),
                s_assetOut);
            Assert.IsTrue(
                File.Exists(PhysicalPath(MoveResDestinationPath)),
                "the first Execute did not write the destination");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a asset.move whose snapshot outlived its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the guid and content-hash evidence survives the session " +
                "boundary");
            Assert.AreEqual(
                "at-02 move fixture\n",
                File.ReadAllText(PhysicalPath(MoveResDestinationPath)),
                "the artifact is exactly what the step moved — nothing re-ran");
            Assert.IsFalse(
                File.Exists(PhysicalPath(MoveResSourcePath)),
                "the source stayed moved away");
        }

        // unity.asset.copy: the copy's recorded guid, localId and content
        // hash re-identify the destination across the restart — Completed,
        // and the resumed Run adopts the step's outputs.
        [Test]
        public void Copy_RestartedSessionResumes()
        {
            var fixture1 = NewFixture();
            var guid = CreateTextAsset(CopyResSourcePath, "at-02 copy fixture\n");
            var runId = ParkStartedOnly(
                fixture1, CopyAssetOperation.OperationId, '0',
                MoveInputs(guid, CopyResSourcePath, CopyResDestinationPath),
                s_assetOut);
            Assert.IsTrue(
                File.Exists(PhysicalPath(CopyResDestinationPath)),
                "the first Execute did not write the copy");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a asset.copy whose snapshot outlived its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the file-identity evidence survives the session boundary");
            Assert.AreEqual(
                "at-02 copy fixture\n",
                File.ReadAllText(PhysicalPath(CopyResDestinationPath)),
                "the copy is exactly what the step wrote — nothing re-ran");
            Assert.AreEqual(
                "at-02 copy fixture\n",
                File.ReadAllText(PhysicalPath(CopyResSourcePath)),
                "the read-only source stays untouched");
        }

        // unity.asset.delete: the absent guid, the freed path and the
        // intact sibling list re-identify the row across the restart —
        // Completed, and nothing re-runs to trash the path again.
        [Test]
        public void Delete_RestartedSessionResumes()
        {
            var fixture1 = NewFixture();
            var guid = CreateTextAsset(DeleteResPath, "at-02 delete fixture\n");
            CreateTextAsset(
                ProbeFolder + "/At02DeleteNeighbour.txt", "neighbour\n");
            var runId = ParkStartedOnly(
                fixture1, DeleteAssetOperation.OperationId, '2',
                DeleteInputs(guid, DeleteResPath));
            Assert.IsFalse(
                File.Exists(PhysicalPath(DeleteResPath)),
                "the first Execute did not trash the target");
            TruncateThrough(StoreRoot, runId, "\"type\":\"step_started\"", "\"step\":0");
            AssertStartedOnlyTail(StoreRoot, runId, 0);
            DisposeFixture(fixture1);
            SimulateEditorRestart(StoreRoot, runId);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            AssertResumed(
                StoreRoot, fixture2, runId,
                "a asset.delete whose snapshot outlived its session");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                "the absence and sibling evidence survives the session " +
                "boundary");
            Assert.IsFalse(
                File.Exists(PhysicalPath(DeleteResPath)),
                "the trashed artifact stays gone — nothing re-ran");
            Assert.IsTrue(
                File.Exists(PhysicalPath(ProbeFolder + "/At02DeleteNeighbour.txt")),
                "the sibling evidence stays intact");
        }

        // unity.asset.import — the group's Waitable: a Run parked at the
        // import's own step_waiting with the persisted resume token is not
        // an interrupted step at all — recovery re-attaches the suspended
        // Run, and the next ticks poll the persisted wait state to its
        // completion: the asset materializes through Poll, never through
        // a re-executed import.
        [Test]
        public void Import_WaitingStepResumesAcrossHostSwap()
        {
            var fixture1 = NewFixture();
            File.WriteAllText(
                PhysicalPath(ImportWaitedPath), "at-02 waited import\n");
            var runId = ParkImportWaiting(fixture1, ImportInputs(ImportWaitedPath));
            fixture1.Host.FlushJournal();
            DisposeFixture(fixture1);

            var fixture2 = NewFixture();
            var report = Recover(fixture2);
            Assert.IsTrue(report.ExclusionHeld);
            Tick(fixture2);
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                "the resumed wait must settle step 0 through Poll; journal: " +
                string.Join(" | ", Lines(StoreRoot, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    StoreRoot, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                "a resumed wait is not a NeedsReview verdict");
            Assert.AreEqual(
                RunState.Running, fixture2.Host.Coordinator.StateOf(runId),
                "the settled import Run parks at its hold step");
            Assert.IsNotNull(
                AssetDatabase.LoadMainAssetAtPath(ImportWaitedPath),
                "the waited import materialized the asset");
        }

        // ── SPEC §F5 across a real domain reload ────────────────────────

        // The SPEC §T.3 reload case the AT-02 check requires: all five
        // Reconcilable Operations of the group, each parked started-only
        // with its evidence disturbed, must still be decided Unknown and
        // end NeedsReview in the domain the compile creates — never an
        // automatic success and never an automatic re-execution (§F0).
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator StartedOnlyDisturbedBecomesNeedsReviewAcrossTheRealReload()
        {
            var before = StartedOnlyGroupPhase();
            Crumb("phase-1 ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True,
                "pre-reload phase failed: " + before.Failure);

            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

            var after = StartedOnlyGroupPhaseAfter();
            Crumb("phase-2 ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True,
                "post-reload phase failed: " + after.Failure);
        }

        // One phase-1 host on a Run Store of its own — never installed,
        // so no editor-update tick can pump its parked hold past the
        // truncation point, and never disposed on the pass path: the OS
        // exclusion its parked Run holds is the process-held handle the
        // hand-off seam carries across the domain reload.
        private static RunnerHostFixture GroupFixture(
            List<RunnerHostFixture> fixtures, int index)
        {
            var fixture = new RunnerHostFixture(StoreRootFor(s_groupKeys[index]));
            fixtures.Add(fixture);
            RegisterAll(fixture);
            return fixture;
        }

        private static void DisposeFixtures(List<RunnerHostFixture> fixtures)
        {
            for (var i = 0; i < fixtures.Count; i++)
            {
                fixtures[i].Dispose();
            }
            fixtures.Clear();
        }

        // Phase 1: each [op -> hold] Run — one per Run Store, the SPEC
        // §E2 single-mutating-Run shape — executes step 0 and waits on
        // the hold; the evidence row it journaled is disturbed in the
        // world, and every journal is cut back to the bare
        // step_started{step:0} a real interruption would have left.
        private static PhaseResult StartedOnlyGroupPhase()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                var runIds = new string[s_groupKeys.Length];

                CreateProbeShader();
                var runMaterial = ParkStartedOnly(
                    GroupFixture(fixtures, 0),
                    CreateMaterialOperation.OperationId, 'a',
                    MaterialInputs(MaterialPath), s_materialOut);
                if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) == null)
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the create_material Execute did not write the Material");
                }
                DamageMaterial(MaterialPath);
                runIds[0] = runMaterial;

                var runObject = ParkStartedOnly(
                    GroupFixture(fixtures, 1),
                    CreateScriptableObjectOperation.OperationId, 'c',
                    ObjectInputs(ObjectPath), s_objectOut);
                if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(ObjectPath) == null)
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the create_scriptable_object Execute did not write " +
                        "the asset");
                }
                DamageObject(ObjectPath);
                runIds[1] = runObject;

                var moveGuid = CreateTextAsset(MoveSourcePath, "at-02 move fixture\n");
                var runMove = ParkStartedOnly(
                    GroupFixture(fixtures, 2),
                    MoveAssetOperation.OperationId, 'e',
                    MoveInputs(moveGuid, MoveSourcePath, MoveDestinationPath),
                    s_assetOut);
                if (!File.Exists(PhysicalPath(MoveDestinationPath)))
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the move Execute did not write the destination");
                }
                DamageAssetFile(MoveDestinationPath);
                runIds[2] = runMove;

                var copyGuid = CreateTextAsset(CopySourcePath, "at-02 copy fixture\n");
                var runCopy = ParkStartedOnly(
                    GroupFixture(fixtures, 3),
                    CopyAssetOperation.OperationId, '0',
                    MoveInputs(copyGuid, CopySourcePath, CopyDestinationPath),
                    s_assetOut);
                if (!File.Exists(PhysicalPath(CopyDestinationPath)))
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the copy Execute did not write the destination");
                }
                DamageAssetFile(CopyDestinationPath);
                runIds[3] = runCopy;

                var deleteGuid = CreateTextAsset(DeletePath, "at-02 delete fixture\n");
                var runDelete = ParkStartedOnly(
                    GroupFixture(fixtures, 4),
                    DeleteAssetOperation.OperationId, '2',
                    DeleteInputs(deleteGuid, DeletePath));
                if (File.Exists(PhysicalPath(DeletePath)))
                {
                    DisposeFixtures(fixtures);
                    return PhaseResult.Fail(
                        "the delete Execute did not trash the target");
                }
                ReoccupyPath(DeletePath);
                runIds[4] = runDelete;

                // The parked holds need no ticks stopped: a phase-1 host
                // is never installed, so nothing pumps a step record past
                // the truncated tail — each journal is cut back to the
                // bare step_started{step:0} in its own store.
                for (var i = 0; i < fixtures.Count; i++)
                {
                    TruncateThrough(
                        fixtures[i].Root, runIds[i],
                        "\"type\":\"step_started\"", "\"step\":0");
                    AssertStartedOnlyTail(fixtures[i].Root, runIds[i], 0);
                }
                WriteState(runIds);
                ArmReload();
                Crumb("phase-1 complete: five disturbed started-only runs, " +
                    "material=" + runMaterial + " object=" + runObject +
                    " move=" + runMove + " copy=" + runCopy +
                    " delete=" + runDelete);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Phase 2: same Editor session (SessionState survives the reload),
        // same live objects — a fresh-domain §F4 pass per Run Store reads
        // one started-only journal and one disturbed world: the surviving
        // OS handle re-adopts, the exclusion passes to the Run lock.json
        // names, and each verdict is Unknown — NeedsReview, no
        // run_resumed, no re-execution — with every disturbed artifact
        // exactly as the disturbance left it.
        private static PhaseResult StartedOnlyGroupPhaseAfter()
        {
            var fixtures = new List<RunnerHostFixture>();
            try
            {
                var persisted = ReadState();
                Assert.AreEqual(
                    s_groupKeys.Length, persisted.Length,
                    "the phase-1 state file lost a run id");

                for (var i = 0; i < s_groupKeys.Length; i++)
                {
                    var fixture = new RunnerHostFixture(
                        StoreRootFor(s_groupKeys[i]));
                    fixtures.Add(fixture);
                    RegisterAll(fixture);
                    fixture.Editor.IsCompiling = EditorApplication.isCompiling;
                    AssertStartedOnlyTail(fixture.Root, persisted[i], 0);
                }
                var names = new[]
                {
                    "asset.create_material", "asset.create_scriptable_object",
                    "asset.move", "asset.copy", "asset.delete",
                };
                for (var i = 0; i < fixtures.Count; i++)
                {
                    var report = Recover(fixtures[i]);
                    Assert.IsTrue(
                        report.ExclusionHeld,
                        "the " + s_groupKeys[i] +
                            " store's recovery pass could not take its exclusion");
                    AssertNeedsReview(
                        fixtures[i].Root, fixtures[i], persisted[i],
                        "a disturbed " + names[i] + " evidence row across a reload");
                }

                Assert.AreEqual(
                    "Standard",
                    AssetDatabase.LoadAssetAtPath<Material>(MaterialPath)
                        .shader.name,
                    "create_material: NeedsReview leaves the foreign artifact");
                Assert.AreEqual(
                    typeof(ProbeAsset),
                    AssetDatabase.LoadAssetAtPath<ScriptableObject>(ObjectPath)
                        .GetType(),
                    "create_scriptable_object: NeedsReview leaves the foreign " +
                    "artifact");
                Assert.AreEqual(
                    DisturbedContent,
                    File.ReadAllText(PhysicalPath(MoveDestinationPath)),
                    "move: NeedsReview leaves the foreign destination");
                Assert.AreEqual(
                    DisturbedContent,
                    File.ReadAllText(PhysicalPath(CopyDestinationPath)),
                    "copy: NeedsReview leaves the foreign destination");
                Assert.AreEqual(
                    DisturbedContent,
                    File.ReadAllText(PhysicalPath(DeletePath)),
                    "delete: NeedsReview leaves the re-occupied path");
                DisposeFixtures(fixtures);
                Crumb("phase-2 complete: five NeedsReview verdicts");
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                DisposeFixtures(fixtures);
                return PhaseResult.Fail(
                    "phase-2 threw " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
