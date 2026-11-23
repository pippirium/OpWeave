#nullable enable

// T-W2.7-02 [Edit] tests: local:T-W2.7-02:asset-import-wait — the SPEC §T.3
// domain-reload case: a Waiting asset.import step whose resumeToken is the
// only surviving state completes after a real domain reload. The
// [UnityTest] holds nothing across the reload (UTF resurrects the iterator
// but not its fields), so each phase is a static helper and the persisted
// input — the resumeToken — lives under
// <project>/Library/OpWeaveImportProbe/. The marker script that forces the
// recompile lives under Assets/Editor/ and its folder is removed by
// [UnityTearDown] — the RefResolverReloadTests pattern.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// SPEC §T.3 / §G4: the asset-import wait survives a domain reload —
    /// the condition is reconstructed from the persisted resumeToken alone.
    /// </summary>
    internal sealed class AssetImportReloadTests
    {
        private const string MarkerFolder = "Assets/Editor/OpWeaveImportProbe";
        private const string ProbeFolder = "Assets/OpWeaveImportProbe";
        private const string ProbePath = ProbeFolder + "/probe.txt";

        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveImportProbe\n{\n" +
            "    internal static class ReloadMarker\n" +
            "    {\n" +
            "        public const int Value = 1;\n" +
            "    }\n" +
            "}\n";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir => Path.Combine(ProjectRoot, "Library", "OpWeaveImportProbe");

        private static string StatePath => Path.Combine(ProbeDir, "import-state.txt");

        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private static void Crumb(string message) => UnityEngine.Debug.Log("[G4] " + message);

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
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
                assetsDirty = true;
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

        // The reload-during-Waiting case of
        // local:T-W2.7-02:asset-import-wait: Execute queues the import and
        // returns the resumeToken; that token — persisted like the journal's
        // StepWaiting record keeps it — is the only thing the post-reload
        // phase has. A fresh condition and a fresh Operation rebuild the
        // wait from the token alone and the step completes.
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator WaitingImport_CompletesAfterDomainReload_FromTokenOnly()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 returned ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True,
                "pre-reload phase failed: " + phase1.Failure);

            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

            var phase2 = RunPhaseAfterReload();
            Crumb("phase-2 returned ok=" + phase2.Ok + " failure=" + phase2.Failure);
            Assert.That(phase2.Ok, Is.True,
                "post-reload phase failed: " + phase2.Failure);

            // The step can keep waiting right after the reload — the import
            // may still be settling — so Poll until it concludes.
            var finished = RunPhaseUntilDone();
            Assert.That(finished.Ok, Is.True,
                "the wait did not conclude after the reload: " + finished.Failure);
        }

        // Phase 1: write the probe file, Execute the Operation to its
        // Waiting, persist the resumeToken it produced, then write the
        // marker script that forces the reload.
        private static ProbePhaseResult RunPhaseBeforeReload()
        {
            try
            {
                Crumb("phase-1 begin: executing the import step");
                if (!AssetDatabase.IsValidFolder(ProbeFolder))
                {
                    AssetDatabase.CreateFolder("Assets", "OpWeaveImportProbe");
                }
                File.WriteAllText(PhysicalPath(ProbePath), "opweave reload probe\n");

                var op = new ImportAssetOperation();
                var result = op.Execute(Context());
                if (result.Kind != StepResultKind.Waiting)
                {
                    return ProbePhaseResult.Fail(
                        "Execute did not go Waiting: " + result.Kind +
                        (result.Error is null ? string.Empty : " " + result.Error.Code));
                }
                var token = result.ResumeToken!.CanonicalText;
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(StatePath, token);
                Crumb("phase-1 persisted resumeToken=" + token);

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

        // Phase 2 (new domain): read the persisted resumeToken — the only
        // surviving state — rebuild the §G4 condition from it alone and ask
        // it once: the proof that the wait state needs nothing but the token.
        private static ProbePhaseResult RunPhaseAfterReload()
        {
            try
            {
                Crumb("phase-2 begin: reading persisted resumeToken");
                if (!File.Exists(StatePath))
                {
                    return ProbePhaseResult.Fail("the persisted resumeToken is missing");
                }
                var parsed = JsonValue.Parse(File.ReadAllText(StatePath));
                if (!parsed.Ok)
                {
                    return ProbePhaseResult.Fail("the persisted resumeToken did not re-parse");
                }
                var token = parsed.Value!;

                var verdict = new AssetImportedCondition().Check(token);
                Crumb("phase-2 fresh-condition verdict=" + verdict.Kind);
                if (verdict.Kind == WaitStatusKind.Failed)
                {
                    return ProbePhaseResult.Fail(
                        "the restored condition failed the import: " + verdict.Error!.Code);
                }
                Crumb("phase-2 done: the token alone rebuilt the wait (" + verdict.Kind + ")");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-2 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("phase-2 threw an unexpected exception: " + e);
            }
        }

        // Phase 3 (still the new domain): drive a fresh Operation's Poll
        // with the persisted resumeToken until the step concludes — the
        // pass condition of the ticket is Done after the reload, output set.
        private static ProbePhaseResult RunPhaseUntilDone()
        {
            try
            {
                var parsed = JsonValue.Parse(File.ReadAllText(StatePath));
                if (!parsed.Ok)
                {
                    return ProbePhaseResult.Fail("the persisted resumeToken did not re-parse");
                }
                var token = parsed.Value!;

                var op = new ImportAssetOperation();
                var context = Context(token);
                var result = op.Poll(context);
                // The import may still be settling right after the reload;
                // only Poll again is allowed, so keep the last Waiting token.
                var guard = 0;
                while (result.Kind == StepResultKind.Waiting && guard++ < 50)
                {
                    token = result.ResumeToken!;
                    context = Context(token);
                    result = op.Poll(context);
                }

                if (result.Kind == StepResultKind.Failed)
                {
                    return ProbePhaseResult.Fail(
                        "the wait failed after the reload: " + result.Error!.Code +
                        " " + result.Error.Message);
                }
                if (result.Kind != StepResultKind.Succeeded)
                {
                    return ProbePhaseResult.Fail(
                        "the wait is still Pending after the reload");
                }
                var emitted = context.Outputs[ImportAssetOperation.AssetPort].ToToken();
                var path = (string?)emitted["path"];
                if (!string.Equals(path, ProbePath, StringComparison.Ordinal))
                {
                    return ProbePhaseResult.Fail("the step output names " + path);
                }
                if (AssetDatabase.LoadMainAssetAtPath(ProbePath) is null)
                {
                    return ProbePhaseResult.Fail("the probe asset did not import");
                }
                Crumb("phase-3 done: the step Succeeded with output " + path);
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                Crumb("phase-3 threw " + e.GetType().Name + ": " + e.Message);
                return ProbePhaseResult.Fail("phase-3 threw an unexpected exception: " + e);
            }
        }

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(
                Application.dataPath,
                assetPath.Substring("Assets/".Length)
                    .Replace('/', Path.DirectorySeparatorChar));

        private static UnityStepContext Context(JsonValue? resumeToken = null) =>
            new UnityStepContext(
                new Dictionary<string, InputValue>
                {
                    [ImportAssetOperation.PathPort] = InputValue.Present(
                        Json("\"" + ProbePath + "\"")),
                },
                new Dictionary<string, string>
                { [ImportAssetOperation.PathPort] = "unity.AssetPath" },
                new Dictionary<string, string>
                { [ImportAssetOperation.AssetPort] = "unity.AssetRef<unity.Object>" },
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "import"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                resumeToken,
                null);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }
    }
}
