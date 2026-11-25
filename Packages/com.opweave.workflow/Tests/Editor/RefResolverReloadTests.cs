#nullable enable

// T-W2.3-01 [Edit] tests (local:T-W2.3-01:ref-resolution): the SPEC §G2
// session 'handle' resolving to the same live object across a real domain
// reload — on 6000.0 the int InstanceID, on 6000.5+ the EntityId (§T.4
// L-04). The [UnityTest] holds nothing across the reload: UTF resurrects
// the iterator but not its fields, so each phase is a static helper and
// every input the second phase needs is persisted under
// <project>/Library/OpWeaveRefProbe/ — the same plumbing the
// CoordinatorRunLockReloadTests use. The marker script lives under
// Assets/Editor/ and its folder is removed by [UnityTearDown].
using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// SPEC §G2 [Edit] test: "ドメインリロード後に handle で解決できる" —
    /// a session handle recorded before a domain reload still resolves to
    /// the same GameObject afterwards, on both Editor generations.
    /// </summary>
    internal sealed class RefResolverReloadTests
    {
        private const string MarkerFolder = "Assets/Editor/OpWeaveRefProbe";
        private const string ProbeAssets = "Assets/OpWeaveRefProbe";
        private const string SingleProbePath = ProbeAssets + "/single.unity";
        private const string ProbeScenePath = ProbeAssets + "/reload.unity";

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveRefProbe\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir => Path.Combine(ProjectRoot, "Library", "OpWeaveRefProbe");

        private static string StatePath => Path.Combine(ProbeDir, "reload-state.txt");

        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // T-W2.4-09: a per-Editor-session token phase-1 stores in SessionState
        // and writes as the probe file's last line; phase-2 reads both to prove
        // the file belongs to this session's phase-1 rather than residue of an
        // earlier run (the ChangeSetDomainReloadTests pattern).
        private const string ProbeTokenKey = "OpWeave.W2409.RefResolver.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume is
        // detected. leg is a method local, so a real reload resets it; the
        // this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static void Crumb(string message) => UnityEngine.Debug.Log("[G2] " + message);

        // Same shape as the L-14 probe: an unthrowable phase result the
        // iterator can assert on after the reload.
        [Serializable]
        private sealed class ProbePhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static ProbePhaseResult Pass() => new ProbePhaseResult { Ok = true };

            public static ProbePhaseResult Fail(string message) =>
                new ProbePhaseResult { Ok = false, Failure = message };
        }

        private static void CleanupProbeArtifacts()
        {
            var assetsDirty = false;
            if (AssetDatabase.IsValidFolder(ProbeAssets))
            {
                AssetDatabase.DeleteAsset(ProbeAssets);
                assetsDirty = true;
            }
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var open = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (open.isLoaded &&
                    open.path.StartsWith(ProbeAssets + "/", StringComparison.Ordinal))
                {
                    EditorSceneManager.CloseScene(open, true);
                }
            }
            if (AssetDatabase.IsValidFolder(MarkerFolder))
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
                assetsDirty = true;
            }
            if (Directory.Exists(MarkerDisk))
            {
                Directory.Delete(MarkerDisk, recursive: true);
                assetsDirty = true;
            }
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a held file is left for the next run */ }
            }
            if (assetsDirty)
            {
                AssetDatabase.Refresh();
            }
        }

        // Setup and teardown must never yield an ExpectDomainReload
        // instruction (RecompileScripts): UTF's PrepareForDomainReload records
        // the CURRENT TestEnumerator's program counter — still a different
        // test's method enumerator during setup and teardown — and the resume
        // then applies that stale PC to the next test method's fresh
        // enumerator, jumping it past phase-1 entirely when the captured
        // enumerator was abandoned mid-state. The probe-asset deletions queue
        // their compile under the runner's LockReloadAssemblies, so the reload
        // they need drains at the next test body's own
        // `yield return new RecompileScripts()`, whose captured PC is the
        // correct one.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup cleanup enter; probeDir=" + Directory.Exists(ProbeDir));
            CleanupProbeArtifacts();
            yield break;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            Crumb("teardown cleanup enter; probeDir=" + Directory.Exists(ProbeDir));
            CleanupProbeArtifacts();
            yield break;
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator SessionHandle_ResolvesAfterDomainReload()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 returned ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True,
                "pre-reload phase failed: " + phase1.Failure);
            Crumb("phase-1 passed; yielding RecompileScripts");

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
                Crumb("iterator resumed; domain sentinel alive=" + DomainSentinelAlive);
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
                CleanupProbeArtifacts();
                var redrive = RunPhaseBeforeReload();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var phase2 = RunPhaseAfterReload();
            Crumb("phase-2 returned ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True,
                "post-reload phase failed: " + phase2.Failure);
        }

        // Phase 1: open a saved probe scene (a session handle is only
        // meaningful next to a scene object that survives the reload), add
        // the target GameObject, save, record the handle and the object's
        // identity to Library/, then write the marker script that forces
        // the reload.
        private static ProbePhaseResult RunPhaseBeforeReload()
        {
            try
            {
                Crumb("phase-1 begin: building saved probe scene");
                if (!AssetDatabase.IsValidFolder(ProbeAssets))
                {
                    AssetDatabase.CreateFolder("Assets", "OpWeaveRefProbe");
                }
                // An unsaved untitled scene blocks NewScene(Additive):
                // replace the runner's scene set with a saved single first
                // (the EditorRequirementEvaluatorTests pattern).
                var single = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (!EditorSceneManager.SaveScene(single, SingleProbePath))
                {
                    return ProbePhaseResult.Fail("the replacement single scene did not save");
                }
                var scene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                var go = new GameObject("G2ReloadTarget");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, scene);
                var child = new GameObject("G2ReloadChild");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(child, scene);
                child.transform.SetParent(go.transform, worldPositionStays: false);
                if (!EditorSceneManager.SaveScene(scene, ProbeScenePath))
                {
                    return ProbePhaseResult.Fail("the probe scene did not save");
                }

                var handle = SessionHandle.Write(child).ToString();
                var token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(StatePath,
                    ProbeScenePath + "\n" + "/G2ReloadTarget/G2ReloadChild" + "\n" +
                    handle + "\n" + token + "\n");
                Crumb("phase-1 recorded handle=" + handle + " to " + StatePath);

                Directory.CreateDirectory(MarkerDisk);
                File.WriteAllText(
                    Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
                AssetDatabase.Refresh();
                ArmDomainSentinel();
                Crumb("phase-1 complete: marker written, sentinel armed");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-1 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("phase-1 threw an unexpected exception: " + e);
            }
        }

        // Phase 2 (new domain): re-read the recorded handle and hierarchy
        // path, re-open the scene list and resolve the ref — the §G2
        // contract is that the same session handle still resolves after a
        // domain reload.
        private static ProbePhaseResult RunPhaseAfterReload()
        {
            try
            {
                Crumb("phase-2 begin: reading state");
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(StatePath))
                {
                    return ProbePhaseResult.Fail(
                        expectedToken.Length == 0
                            ? "phase-1 never executed in this Editor session (" +
                                StatePath + " was never written; probeDir exists=" +
                                Directory.Exists(ProbeDir) + ")"
                            : "the persisted probe state " + StatePath +
                                " was deleted after phase-1 wrote it");
                }
                var lines = File.ReadAllLines(StatePath);
                if (lines.Length < 4)
                {
                    return ProbePhaseResult.Fail("the persisted probe state is truncated");
                }
                if (expectedToken.Length == 0 || lines[3] != expectedToken)
                {
                    return ProbePhaseResult.Fail(
                        "the persisted probe state is stale — a previous Editor " +
                        "session wrote " + StatePath);
                }
                var scenePath = lines[0];
                var hierarchyPath = lines[1];
                var handleText = lines[2];
                Crumb("phase-2 scene=" + scenePath + " path=" + hierarchyPath +
                    " handle=" + handleText);

                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
                if (!scene.isLoaded)
                {
                    return ProbePhaseResult.Fail(
                        "the saved probe scene is not loaded after the reload");
                }

                var resolver = new RefResolver();
                // The digit-string spelling also covers a 6000.5 EntityId
                // that exceeds Int64 (L-04); the Codec accepts both forms.
                var parsed = JsonValue.Parse(
                    "{\"kind\":\"gameObject\",\"handle\":\"" + handleText + "\"}");
                if (!parsed.Ok)
                {
                    return ProbePhaseResult.Fail("the recorded handle did not re-parse");
                }
                var resolved = resolver.Resolve<GameObject>(parsed.Value!);
                if (!resolved.Ok)
                {
                    var codes = resolved.Diagnostics.Count == 0
                        ? "<no diagnostics>"
                        : resolved.Diagnostics[0].Code + " " +
                            resolved.Diagnostics[0].Message;
                    return ProbePhaseResult.Fail(
                        "the recorded handle did not resolve after the reload: " + codes);
                }
                var go = resolved.Value!;
                if (go.name != "G2ReloadChild" || go.scene.path != scenePath)
                {
                    return ProbePhaseResult.Fail(
                        "the handle resolved a different object: " + go.name +
                        " in " + go.scene.path);
                }
                Crumb("phase-2 resolved the same object by handle");

                // The full Ref also still resolves: handle + path.
                var byPath = resolver.Resolve<GameObject>(JsonValue.From(new JObject
                {
                    ["kind"] = "gameObject",
                    ["scene"] = new JObject { ["kind"] = "scene", ["path"] = scenePath },
                    ["path"] = hierarchyPath,
                }));
                if (!byPath.Ok || !ReferenceEquals(byPath.Value, go))
                {
                    return ProbePhaseResult.Fail(
                        "the scene+path ref did not resolve to the same object");
                }
                Crumb("phase-2 resolved by scene+path too; done");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-2 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("phase-2 threw an unexpected exception: " + e);
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
            if (!File.Exists(StatePath))
            {
                return false;
            }
            var expectedToken = SessionState.GetString(ProbeTokenKey, "");
            if (expectedToken.Length == 0)
            {
                return false;
            }
            var lines = File.ReadAllLines(StatePath);
            return lines.Length > 0 && lines[lines.Length - 1] == expectedToken;
        }
    }
}
