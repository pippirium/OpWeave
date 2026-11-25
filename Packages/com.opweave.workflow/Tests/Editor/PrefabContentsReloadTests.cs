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

        // T-W2.4-09: a per-Editor-session token phase-1 stores in SessionState
        // and writes as the probe file's last line; phase-2 reads both to prove
        // the file belongs to this session's phase-1 rather than residue of an
        // earlier run (the ChangeSetDomainReloadTests pattern).
        private const string ProbeTokenKey = "OpWeave.W2409.PrefabContents.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume is
        // detected. leg is a method local, so a real reload resets it; the
        // this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

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
        public IEnumerator OpenContents_AcrossDomainReload_ReconcilesCompleted()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 returned ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True, "pre-reload phase failed: " + phase1.Failure);
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
                var token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
                File.WriteAllText(
                    StatePath,
                    path + "\n" + guid + "\n" + preflight.Snapshot.CanonicalText +
                        "\n" + token + "\n");
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
