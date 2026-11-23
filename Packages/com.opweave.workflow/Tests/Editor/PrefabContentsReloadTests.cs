#nullable enable

// T-W2.6-03 [Edit] test (local:T-W2.6-03:prefab-contents-reconcile, SPEC §T.3):
// a real domain reload between the step's journaled acquisition and its
// recovery. open_contents' Completed evidence is exactly what survives a
// reload — the §F2 fold's acquisition record and the contents preview scene
// with its unsaved modifications (SPEC §T.4 L-06) — while the managed
// reference and every managed cache do not. The [UnityTest] holds nothing
// across the reload: UTF resurrects the iterator but not its fields, so each
// phase is a static helper and everything the second phase needs (the target
// path, its guid and the Preflight snapshot) is persisted under
// <project>/Library/OpWeavePrefabContentsProbe/ — the plumbing of
// RefResolverReloadTests. The marker script that forces the compile lives
// under Assets/Editor/ and its folder is removed by [UnityTearDown].
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.6-03:prefab-contents-reconcile — the §F5 Completed verdict of
    /// <c>unity.prefab.open_contents</c> across a real domain reload.
    /// </summary>
    internal sealed class PrefabContentsReloadTests
    {
        private const string OpenId = "unity.prefab.open_contents";
        private const string OperationsAssembly = "OpWeave.Operations";
        private const string ProbeFile = "reload.prefab";
        private const string MarkerFolder = "Assets/Editor/OpWeavePrefabContentsProbe";

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeavePrefabContentsProbe\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeavePrefabContentsProbe");

        private static string StatePath => Path.Combine(ProbeDir, "reload-state.txt");

        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[G6-F5] " + message);

        // An unthrowable phase result the iterator can assert on after the
        // reload — an exception before the reload would otherwise be swallowed
        // by the domain it is thrown in.
        [Serializable]
        private sealed class ProbePhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static ProbePhaseResult Pass() => new ProbePhaseResult { Ok = true };

            public static ProbePhaseResult Fail(string message) =>
                new ProbePhaseResult { Ok = false, Failure = message };
        }

        private static bool MarkerPresent =>
            AssetDatabase.IsValidFolder(MarkerFolder) || Directory.Exists(MarkerDisk);

        private static void CleanupProbeArtifacts()
        {
            PrefabContentsFixture.UnloadAllOpenedContents();
            if (AssetDatabase.IsValidFolder(PrefabContentsFixture.ProbeFolder))
            {
                AssetDatabase.DeleteAsset(PrefabContentsFixture.ProbeFolder);
            }
            if (AssetDatabase.IsValidFolder(MarkerFolder))
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
            }
            if (Directory.Exists(MarkerDisk))
            {
                try { Directory.Delete(MarkerDisk, recursive: true); }
                catch (Exception) { /* a held file is left for the next run */ }
            }
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* a held file is left for the next run */ }
            }
            AssetDatabase.Refresh();
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var markerWasPresent = MarkerPresent;
            CleanupProbeArtifacts();
            if (markerWasPresent)
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var markerWasPresent = MarkerPresent;
            CleanupProbeArtifacts();
            if (markerWasPresent)
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator OpenContents_AcrossDomainReload_ReconcilesCompleted()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 returned ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True, "pre-reload phase failed: " + phase1.Failure);
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
            Assert.That(phase2.Ok, Is.True, "post-reload phase failed: " + phase2.Failure);
        }

        // Phase 1: create the target Prefab, run the standard open_contents
        // step over it (its acquisition journaled through the step's registry),
        // persist the recovery evidence the §F4 engine would carry, then write
        // the marker script that forces the reload.
        private static ProbePhaseResult RunPhaseBeforeReload()
        {
            try
            {
                Crumb("phase-1 begin: creating the target Prefab");
                var asset = PrefabContentsFixture.CreateProbePrefab(ProbeFile, "Body");
                if (asset == null)
                {
                    return ProbePhaseResult.Fail("the target Prefab did not save");
                }
                var path = PrefabContentsFixture.ProbePath(ProbeFile);
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                {
                    return ProbePhaseResult.Fail("the target Prefab carries no guid");
                }

                var harness = new PrefabContentsFixture.Harness();
                var context = harness.OpenContext(guid, path);
                var operation = Standard(OpenId);
                var preflight = operation.Preflight(context);
                if (preflight.Diagnostics.Count != 0)
                {
                    return ProbePhaseResult.Fail(
                        "Preflight reported " + preflight.Diagnostics[0].Code);
                }
                var executed = operation.Execute(context);
                if (executed.Kind != StepResultKind.Succeeded)
                {
                    return ProbePhaseResult.Fail(
                        "Execute returned " + executed.Kind +
                        (executed.Error is null ? "" : " " + executed.Error.Code));
                }
                if (harness.Records.Count != 1 ||
                    PrefabContentsFixture.Harness.EditingRoots(path).Count != 1)
                {
                    return ProbePhaseResult.Fail(
                        "the acquisition or the opened contents are missing");
                }

                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(
                    StatePath,
                    path + "\n" + guid + "\n" + preflight.Snapshot.CanonicalText);
                Crumb("phase-1 recorded " + path + " (" + guid + ")");

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

        // Phase 2 (new domain): the step's managed state is gone — this reads
        // only what a recovery session has, the journaled acquisition the §F2
        // fold rebuilds and the StepStarted snapshot — and asks Reconcile. The
        // contents and their unsaved modifications survived the reload (L-06),
        // so the row's Completed evidence holds, and the restored output names
        // that one live session. A second session must not have appeared.
        private static ProbePhaseResult RunPhaseAfterReload()
        {
            try
            {
                Crumb("phase-2 begin: reading the persisted recovery evidence");
                if (!File.Exists(StatePath))
                {
                    return ProbePhaseResult.Fail("the persisted probe state is missing");
                }
                var lines = File.ReadAllLines(StatePath);
                if (lines.Length < 3)
                {
                    return ProbePhaseResult.Fail("the persisted probe state is truncated");
                }
                var path = lines[0];
                var guid = lines[1];
                var parsed = JsonValue.Parse(lines[2]);
                if (!parsed.Ok || parsed.Value is null)
                {
                    return ProbePhaseResult.Fail("the persisted snapshot does not parse");
                }
                var snapshot = parsed.Value!;

                var roots = PrefabContentsFixture.Harness.EditingRoots(path);
                if (roots.Count != 1)
                {
                    return ProbePhaseResult.Fail(
                        "the opened contents did not survive the reload (roots=" +
                        roots.Count + ")");
                }
                var before = PrefabContentsOwnership.SessionIdOf(roots[0]);

                var recovery = new PrefabContentsFixture.Harness();
                recovery.SeedFoldedAcquisition(guid, path);
                var verdict = Standard(OpenId).Reconcile(
                    recovery.OpenContext(guid, path), snapshot);

                if (verdict is not ReconcileResult.Completed completed)
                {
                    return ProbePhaseResult.Fail("the verdict was " + verdict.GetType().Name +
                        (verdict is ReconcileResult.Unknown unknown ? ": " + unknown.Reason : ""));
                }
                if (!completed.Outputs.TryGetValue("root", out var restored) ||
                    restored is null)
                {
                    return ProbePhaseResult.Fail("the root output was not restored");
                }
                var scene = restored.ToToken()["scene"]?.ToString();
                if (scene != "prefab-contents:" + guid)
                {
                    return ProbePhaseResult.Fail("the restored Ref's scene key is '" +
                        scene + "'");
                }
                var resolved = new RefResolver(PrefabContentsResources.FindEditingRoot)
                    .Resolve<GameObject>(restored);
                if (!resolved.Ok || resolved.Value is null)
                {
                    return ProbePhaseResult.Fail("the restored Ref does not resolve: " +
                        string.Join(",", Diagnostics(resolved.Diagnostics)));
                }
                if (PrefabContentsOwnership.SessionIdOf(resolved.Value) != before)
                {
                    return ProbePhaseResult.Fail(
                        "the restored Ref names a different object than the live contents");
                }
                var after = PrefabContentsFixture.Harness.EditingRoots(path);
                if (after.Count != 1 ||
                    PrefabContentsOwnership.SessionIdOf(after[0]) != before)
                {
                    return ProbePhaseResult.Fail(
                        "the verdict changed the open sessions (roots=" + after.Count + ")");
                }
                Crumb("phase-2 complete: Completed with the live contents restored");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-2 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("phase-2 threw an unexpected exception: " + e);
            }
        }

        private static string[] Diagnostics(IReadOnlyList<Diagnostic> diagnostics)
        {
            var codes = new string[diagnostics.Count];
            for (var i = 0; i < diagnostics.Count; i++)
            {
                codes[i] = diagnostics[i].Code;
            }
            return codes;
        }

        // The registered standard Operation in the current domain — the cache
        // of the other fixtures cannot cross the reload, and rebuilding is the
        // faithful seam for a recovery session anyway.
        private static IOperation Standard(string id)
        {
            var publication = new RegistryPublication();
            var seams = new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => TypeCache
                    .GetTypesWithAttribute<OpWeaveOperationAttribute>()
                    .Where(type => string.Equals(
                        type.Assembly.GetName().Name, OperationsAssembly,
                        StringComparison.Ordinal)),
                ProviderTypes: () =>
                    TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: type => EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());
            var build = RegistryBuildHost.Rebuild(
                seams, RegistryOperationStage.Production(), publication);
            Assert.That(build.Snapshot, Is.Not.Null);
            var registered = build.Snapshot!.Find(id, 1);
            Assert.That(registered, Is.Not.Null, id + " v1 must register");
            Assert.That(registered!.Implementation, Is.InstanceOf<IOperation>(), id);
            return registered.Implementation!;
        }
    }
}
