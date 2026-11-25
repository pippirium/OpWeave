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

        private static string SessionProbePath => Path.Combine(ProbeDir, "session.txt");

        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // T-W2.4-09: a per-Editor-session token phase-1 stores in SessionState
        // and writes into the session probe file beside the persisted
        // resumeToken (a single JSON document that cannot carry the token);
        // phase-2 reads both to prove the persisted state belongs to this
        // session's phase-1 rather than residue of an earlier run.
        private const string ProbeTokenKey = "OpWeave.W2409.AssetImport.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume is
        // detected. leg is a method local, so a real reload resets it; the
        // this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

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
                var sessionToken = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, sessionToken);
                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(StatePath, token);
                File.WriteAllText(SessionProbePath, sessionToken + "\n");
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
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(StatePath))
                {
                    return ProbePhaseResult.Fail(
                        expectedToken.Length == 0
                            ? "phase-1 never executed in this Editor session (" +
                                StatePath + " was never written; probeDir exists=" +
                                Directory.Exists(ProbeDir) + ")"
                            : "the persisted resumeToken " + StatePath +
                                " was deleted after phase-1 wrote it");
                }
                if (expectedToken.Length == 0 || !File.Exists(SessionProbePath) ||
                    File.ReadAllText(SessionProbePath).Trim() != expectedToken)
                {
                    return ProbePhaseResult.Fail(
                        "the persisted probe state is stale — a previous Editor " +
                        "session wrote " + SessionProbePath);
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

        // The phase-1 probe proves phase-1 ran in THIS Editor session: the
        // session file must exist AND its content must equal the token phase-1
        // stored in SessionState. Both Library/ and SessionState survive the
        // reload, so a stale-resume hijack (phase-1 skipped: no file, no token)
        // and residue of an earlier session (file yes, token no) both fail the
        // check — the caller then re-drives a real phase-1 instead of skipping
        // into phase-2.
        private static bool ProbeWrittenThisSession()
        {
            if (!File.Exists(StatePath) || !File.Exists(SessionProbePath))
            {
                return false;
            }
            var expectedToken = SessionState.GetString(ProbeTokenKey, "");
            return expectedToken.Length > 0 &&
                File.ReadAllText(SessionProbePath).Trim() == expectedToken;
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
