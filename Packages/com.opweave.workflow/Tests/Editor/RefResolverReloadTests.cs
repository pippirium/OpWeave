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

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var markerWasPresent = AssetDatabase.IsValidFolder(MarkerFolder) ||
                Directory.Exists(MarkerDisk);
            CleanupProbeArtifacts();
            if (markerWasPresent)
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var markerWasPresent = AssetDatabase.IsValidFolder(MarkerFolder) ||
                Directory.Exists(MarkerDisk);
            CleanupProbeArtifacts();
            if (markerWasPresent)
            {
                yield return new RecompileScripts(false);
            }
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

            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Crumb("iterator resumed; domain sentinel alive=" + DomainSentinelAlive);
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

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
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(StatePath,
                    ProbeScenePath + "\n" + "/G2ReloadTarget/G2ReloadChild" + "\n" + handle);
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
                if (!File.Exists(StatePath))
                {
                    return ProbePhaseResult.Fail("the persisted probe state is missing");
                }
                var lines = File.ReadAllLines(StatePath);
                if (lines.Length < 3)
                {
                    return ProbePhaseResult.Fail("the persisted probe state is truncated");
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
    }
}
