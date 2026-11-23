#nullable enable

// T-W2.9-07 [Edit] tests: local:T-W2.9-07:create-operation-uuid — the SPEC
// §M.4 tests 1 and 2 on generated IDs: the create dialog's live validation
// (collisions, OW-UNITY-012), the commit-time ID checks driven through the
// injected ID source (A4 grammar, OW-OP-007, duplicates), and the full
// generate → compile → register → run-generated-tests cycle across a
// domain reload (the fixture's own bounded reload instruction in the
// [UnityTest] body and in setup/teardown, SPEC §T.3). The
// [UnityTest] holds nothing across the reload, so each phase is a static
// helper and the probe paths are constants; the probe folder and the
// generated test file are removed by [UnityTearDown].
using System;
using System.Collections;
using System.IO;
using System.Reflection;
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
    /// The SPEC §M.4 acceptance tests of
    /// local:T-W2.9-07:create-operation-uuid.
    /// </summary>
    internal sealed class OperationCreateMenuTests
    {
        // The generated-ID format of SPEC §A4 ("proj.op_" + 32 lowercase
        // hex) used to assert what the menu writes; a concrete sample for
        // the render test.
        private const string GeneratedIdPattern = "\\Aproj\\.op_[0-9a-f]{32}\\z";
        private const string SampleGeneratedId =
            "proj.op_0123456789abcdef0123456789abcdef";
        // Carries the generated ID across the domain reload inside the
        // same Editor session.
        private const string GeneratedIdSessionKey =
            "OpWeave.MenuTests.GeneratedId";
        private const string ProbeDisplayName = "Menu Probe";
        private const string ProbeClassName = "MenuProbeOperation";
        private const string ProbeTestClassName = "MenuProbeOperationTests";
        private const string ProbeFolder = "Assets/OpWeave/Editor/Operations/MenuProbe";
        private const string ProbeFile =
            "Assets/OpWeave/Editor/Operations/MenuProbe/MenuProbeOperation.cs";
        private const string ProbeNamespace = "OpWeave.Custom";
        private const string RuntimeFolder = "Assets/OpWeaveCreateProbeRuntime";
        private const string TestProbeFile =
            "Assets/OpWeave/Editor/Operations/Transform/Tests/MenuProbeOperationTests.cs";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string Absolute(string assetRelative) =>
            Path.Combine(
                ProjectRoot, assetRelative.Replace('/', Path.DirectorySeparatorChar));

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // The post-reload registration poll keeps its state in statics
        // that are assigned only after the reload, and it runs as a
        // method-group predicate that captures nothing: UTF rebuilds
        // the test enumerator with only its program counter restored,
        // so a local captured by a lambda lives in a display class that
        // stays null in the new domain — the NRE the SessionState
        // carries were added against.
        private static string s_pollOperationId = string.Empty;
        private static RegisteredOperation? s_pollResult;

        private static bool PollResultMissing()
        {
            var snapshot = RegistryPublication.Shared.Snapshot;
            if (snapshot is not null && !RegistryPublication.Shared.Stale)
            {
                s_pollResult = snapshot.Find(s_pollOperationId, 1);
            }
            return s_pollResult is null;
        }

        private static OperationCreateRequest NewRequest() =>
            new OperationCreateRequest
            {
                DisplayName = ProbeDisplayName,
                TargetFolder = ProbeFolder,
            };

        private static void DeleteAssetPath(string assetPath)
        {
            if (File.Exists(Absolute(assetPath)))
            {
                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    File.Delete(Absolute(assetPath));
                }
            }
            var meta = Absolute(assetPath) + ".meta";
            if (File.Exists(meta))
            {
                File.Delete(meta);
            }
        }

        // A folder delete also removes the sibling <name>.meta — an orphan
        // folder meta is exactly the kind of leftover a later import sweep
        // turns into churn.
        private static void DeleteFolderAsset(string assetPath)
        {
            if (!AssetDatabase.DeleteAsset(assetPath) &&
                Directory.Exists(Absolute(assetPath)))
            {
                Directory.Delete(Absolute(assetPath), recursive: true);
            }
            var meta = Absolute(assetPath) + ".meta";
            if (File.Exists(meta))
            {
                File.Delete(meta);
            }
        }

        // Deepest first: the files inside the probe folders, then the
        // folders and their .meta files. Returns true only when the
        // cleanup deleted a script or asmdef Unity had already imported
        // — the sole case that queues a recompile + domain reload.
        private static bool CleanupProbeArtifacts()
        {
            var dirty = false;
            var scriptPending = false;
            if (File.Exists(Absolute(ProbeFile)))
            {
                scriptPending |= IsImportedAsset(ProbeFile);
                DeleteAssetPath(ProbeFile);
                dirty = true;
            }
            if (File.Exists(Absolute(TestProbeFile)))
            {
                scriptPending |= IsImportedAsset(TestProbeFile);
                DeleteAssetPath(TestProbeFile);
                dirty = true;
            }
            if (FolderOrMetaExists(ProbeFolder))
            {
                scriptPending |= FolderHasImportedScript(ProbeFolder);
                DeleteFolderAsset(ProbeFolder);
                dirty = true;
            }
            if (FolderOrMetaExists(RuntimeFolder))
            {
                scriptPending |= FolderHasImportedScript(RuntimeFolder);
                DeleteFolderAsset(RuntimeFolder);
                dirty = true;
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            return scriptPending;
        }

        private static bool FolderOrMetaExists(string assetPath) =>
            AssetDatabase.IsValidFolder(assetPath) ||
            Directory.Exists(Absolute(assetPath)) ||
            File.Exists(Absolute(assetPath) + ".meta");

        // True only when Unity already imported the asset — its .meta
        // file is the deterministic marker. Only removing an imported
        // script or asmdef schedules a recompile; deleting a never-
        // imported placeholder or a script-free folder does not.
        private static bool IsImportedAsset(string assetPath) =>
            File.Exists(Absolute(assetPath) + ".meta");

        private static bool FolderHasImportedScript(string assetPath)
        {
            var absolute = Absolute(assetPath);
            if (!Directory.Exists(absolute))
            {
                return false;
            }
            foreach (var file in Directory.GetFiles(
                absolute, "*", SearchOption.AllDirectories))
            {
                if ((file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)) &&
                    File.Exists(file + ".meta"))
                {
                    return true;
                }
            }
            return false;
        }

        // T-W2.9-06: every wait in this fixture is bounded, and a pending
        // script reload is crossed only through BoundedScriptReload — a
        // local IEditModeTestYieldInstruction, the one extension point
        // UTF opens for setup/teardown yields. UTF's own instructions
        // cannot carry a deadline: RecompileScripts.Perform and
        // WaitForDomainReload.Perform each end in an unbounded
        // "while (EditorApplication.isCompiling) yield return null;",
        // WaitForDomainReload additionally throws unless a reload is
        // already pending (the PR #401 race), and no outer enumerator
        // can bound a wait inside the instruction. BoundedScriptReload
        // mirrors RecompileScripts.Perform — one arming frame,
        // AssetDatabase.Refresh, unlock, wait out the compile — but
        // first proves the queued compile started and bounds both its
        // waits on a single deadline that fails naming the phase.
        // A deleted imported script leaves a pending compile/reload
        // behind, which must be crossed here at a reload boundary —
        // never handed to the next fixture. But yielding the reload
        // instruction when NO compile is queued is equally wrong: it
        // declares ExpectDomainReload and unlocks the assemblies, and
        // with no reload following, the next fixture's real reload is
        // swallowed mid-pump on the stale armed state. So the boundary
        // yield happens only for a script-affecting cleanup —
        // scriptPending records this fixture's own — where the
        // instruction proves the pending compile before crossing, so
        // its precondition always holds. Everything else — including
        // the compile/update flags a leaked reload from an earlier
        // fixture can pin (the PR #361 hang) — settles through
        // WaitForEditorIdle, a bounded plain-frame wait that fails
        // naming the phase and the watched state rather than waiting
        // forever.
        private const double WaitTimeoutSec = 240.0;

        private const string EditorBusyState =
            "EditorApplication.isCompiling || EditorApplication.isUpdating";

        // The bounded plain-frame wait the fixture uses for every state
        // that is not itself a reload boundary: while the busy predicate
        // holds, yield a frame; at the deadline, fail with the fixture
        // phase and the state it waited on. It yields nothing but nulls,
        // which is the only enumerator content legal one level down from
        // the setup/teardown enumerator — the reload boundary above must
        // stay at the top level.
        private static IEnumerator WaitWhileBusy(
            string phase, Func<bool> busy, double timeoutSec) =>
            WaitWhileBusy(
                phase, busy, timeoutSec, EditorBusyState + " to clear");

        private static IEnumerator WaitWhileBusy(
            string phase, Func<bool> busy, double timeoutSec, string waitedOn)
        {
            var deadline = EditorApplication.timeSinceStartup + timeoutSec;
            while (busy())
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "OperationCreateMenuTests." + phase + " timed out " +
                        "after " + timeoutSec + " s waiting for " +
                        waitedOn);
                }
                yield return null;
            }
        }

        private static IEnumerator WaitForEditorIdle(string phase) =>
            WaitWhileBusy(
                phase,
                () => EditorApplication.isCompiling || EditorApplication.isUpdating,
                WaitTimeoutSec);

        // RecompileScripts.Perform with deadlines, run by the EditMode
        // runner through the IEditModeTestYieldInstruction interface —
        // so ExpectDomainReload arms UTF's reload bookkeeping exactly as
        // with UTF's own instruction, and the domain reload itself still
        // ends the instruction. The compiling/refresh seams exist only
        // so the regression test can pump Perform() without touching the
        // real asset pipeline.
        private sealed class BoundedScriptReload : IEditModeTestYieldInstruction
        {
            private readonly string m_phase;
            private readonly double m_timeoutSec;
            private readonly Func<bool> m_compiling;
            private readonly Action m_refresh;

            internal BoundedScriptReload(
                string phase, double timeoutSec,
                Func<bool>? compiling = null, Action? refresh = null)
            {
                m_phase = phase;
                m_timeoutSec = timeoutSec;
                m_compiling = compiling ?? (() => EditorApplication.isCompiling);
                m_refresh = refresh ?? AssetDatabase.Refresh;
            }

            public bool ExpectDomainReload => true;
            public bool ExpectedPlaymodeState => false;

            public IEnumerator Perform()
            {
                // UTF yields the same first frame: the runner must see
                // the yielded instruction and arm its expected-domain-
                // reload bookkeeping before the reload can start.
                yield return null;

                var deadline =
                    EditorApplication.timeSinceStartup + m_timeoutSec;

                m_refresh();

                // Prove the queued compile really started before
                // crossing — the same precondition RecompileScripts
                // asserts up front, except Refresh returns before
                // isCompiling goes true, so it gets bounded frames from
                // the same deadline. On expiry nothing is unlocked yet,
                // so no LockReloadAssemblies is needed.
                while (!m_compiling())
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        Assert.Fail(
                            "OperationCreateMenuTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: no script compilation started");
                    }
                    yield return null;
                }

                EditorApplication.UnlockReloadAssemblies();

                while (m_compiling())
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        EditorApplication.LockReloadAssemblies();
                        Assert.Fail(
                            "OperationCreateMenuTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: EditorApplication.isCompiling did " +
                            "not clear");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "OperationCreateMenuTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var scriptPending = CleanupProbeArtifacts();
            if (scriptPending)
            {
                yield return new BoundedScriptReload(
                    nameof(UnitySetUp), WaitTimeoutSec);
            }
            yield return WaitForEditorIdle(nameof(UnitySetUp));
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            SessionState.EraseString(GeneratedIdSessionKey);
            var scriptPending = CleanupProbeArtifacts();
            if (scriptPending)
            {
                yield return new BoundedScriptReload(
                    nameof(UnityTearDown), WaitTimeoutSec);
            }
            yield return WaitForEditorIdle(nameof(UnityTearDown));
        }

        // T-W2.9-06 regression: a setup wait whose condition never clears
        // ends in a failure inside its bound instead of waiting forever.
        // The fixture's own bounded-wait helper is pumped directly with a
        // predicate that cannot clear; nothing touches Editor state.
        [Test]
        public void BoundedSetupWait_BusyNeverClears_FailsAtDeadline()
        {
            const double timeoutSec = 0.25;
            var wait = WaitWhileBusy(nameof(UnitySetUp), () => true, timeoutSec);
            var startedAt = EditorApplication.timeSinceStartup;
            var error = Assert.Throws<AssertionException>(() =>
            {
                while (wait.MoveNext())
                {
                }
            });
            var elapsed = EditorApplication.timeSinceStartup - startedAt;
            Assert.That(error, Is.Not.Null);
            Assert.That(error!.Message, Does.Contain(nameof(UnitySetUp)),
                "the failure message must name the fixture phase");
            Assert.That(error!.Message, Does.Contain(EditorBusyState),
                "the failure message must name the waited-on state");
            Assert.That(elapsed, Is.LessThan(timeoutSec + 10.0),
                "the bounded wait failed " + elapsed + " s in — it must end " +
                "at its " + timeoutSec + " s deadline, not wait forever");
        }

        // T-W2.9-06 regression: the reload-crossing instruction is itself
        // bounded — a compile flag that never clears fails at its
        // deadline instead of waiting forever. Perform() is pumped
        // directly with an injected always-compiling predicate and a
        // no-op refresh, so nothing touches the real asset pipeline;
        // the Unlock the real path performs is balanced by the expiry
        // path's LockReloadAssemblies.
        [Test]
        public void BoundedScriptReload_IsCompilingNeverClears_FailsAtDeadline()
        {
            const double timeoutSec = 0.25;
            var reload = new BoundedScriptReload(
                nameof(UnitySetUp), timeoutSec,
                compiling: () => true, refresh: () => { });
            var perform = reload.Perform();
            Assert.That(perform.MoveNext(), Is.True);
            Assert.That(perform.Current, Is.Null,
                "the first step must be the runner's arming frame");
            var wasCompiling = EditorApplication.isCompiling;
            var startedAt = EditorApplication.timeSinceStartup;
            var error = Assert.Throws<AssertionException>(() =>
            {
                while (perform.MoveNext())
                {
                }
            });
            var elapsed = EditorApplication.timeSinceStartup - startedAt;
            Assert.That(error, Is.Not.Null);
            Assert.That(error!.Message, Does.Contain(nameof(UnitySetUp)),
                "the failure message must name the fixture phase");
            Assert.That(error!.Message,
                Does.Contain("EditorApplication.isCompiling did not clear"),
                "the failure message must name the compile state");
            Assert.That(elapsed, Is.LessThan(timeoutSec + 10.0),
                "the bounded reload wait failed " + elapsed + " s in — " +
                "it must end at its " + timeoutSec +
                " s deadline, not wait forever");
            Assert.That(EditorApplication.isCompiling,
                Is.EqualTo(wasCompiling),
                "the injected predicate must leave the real compile " +
                "state untouched");
        }

        // ── SPEC §M.4 test 1 (dialog validation) ─────────────────────────

        [Test]
        public void MenuItem_IsRegisteredUnderAssetsCreate()
        {
            var method = typeof(OperationCreateMenu).GetMethod(
                "Open", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var items = method!.GetCustomAttributes(typeof(MenuItem), inherit: false);
            Assert.That(items, Has.Length.EqualTo(1));
            Assert.That(((MenuItem)items[0]).menuItem,
                Is.EqualTo(OperationCreateMenu.MenuPath));
            Assert.That(OperationCreateMenu.MenuPath,
                Is.EqualTo("Assets/Create/OpWeave/Operation"));
        }

        [Test]
        public void Plan_ValidRequest_HasNoErrorsAndExpectedLayout()
        {
            var request = NewRequest();
            request.Effects.Add(EffectNames.Read);
            request.Summary = "A generated probe.";
            var plan = OperationCreatePlanner.Plan(request);
            Assert.That(plan.HasErrors, Is.False,
                "unexpected issues: " + string.Join("; ", plan.Issues));
            Assert.That(plan.CanWrite, Is.True);
            Assert.That(plan.ClassName, Is.EqualTo(ProbeClassName));
            Assert.That(plan.FilePath, Is.EqualTo(ProbeFile));
            Assert.That(plan.NamespaceName, Is.EqualTo(ProbeNamespace));
            Assert.That(plan.TargetAssemblyName,
                Is.EqualTo("Assembly-CSharp-Editor"));
            Assert.That(plan.TestFilePath, Is.EqualTo(TestProbeFile));
            Assert.That(plan.TestClassName, Is.EqualTo(ProbeTestClassName));
            Assert.That(plan.TestNamespace, Is.EqualTo("OpWeave.Custom.Tests"));
        }

        // Commit-time check (SPEC §M.4 item 2): the dialog takes no ID
        // input, so the OW-OP-007 path is driven through the injected ID
        // source. A reserved draw fails immediately with no file written.
        [Test]
        public void Write_ReservedNamespaceId_ReportsOwOp007()
        {
            foreach (var id in new[] { "core.menu.probe", "unity.menu.probe", "opweave.menu.probe" })
            {
                var calls = 0;
                var plan = OperationCreatePlanner.Plan(NewRequest());
                Assert.That(plan.CanWrite, Is.True,
                    "the plan must be writable before the ID check: " +
                    string.Join("; ", plan.Issues));
                var result = OperationCreateWriter.Write(
                    plan, () => { calls++; return id; });
                Assert.That(result.Ok, Is.False);
                Assert.That(
                    result.Errors.Exists(e => e.Contains(ErrorCodes.OW_OP_007)),
                    Is.True, id + " must report OW-OP-007, got: " +
                    string.Join("; ", result.Errors));
                Assert.That(result.WrittenAssets, Is.Empty);
                Assert.That(result.OperationId, Is.Null);
                Assert.That(calls, Is.EqualTo(1),
                    "a reserved draw fails on the spot, not by retry");
                Assert.IsFalse(File.Exists(Absolute(ProbeFile)),
                    "no file may be written for a reserved ID");
            }
        }

        // Commit-time check: a malformed draw (never producible by the
        // default UUID source) is rejected by the A4 grammar check before
        // any file is written — a grammar error, not OW-OP-007.
        [Test]
        public void Write_MalformedGeneratedId_ReportsGrammarError()
        {
            foreach (var id in new[] { "proj", "Proj.menu.probe", "proj..x", "proj.x-" })
            {
                var plan = OperationCreatePlanner.Plan(NewRequest());
                Assert.That(plan.CanWrite, Is.True,
                    "the plan must be writable before the ID check: " +
                    string.Join("; ", plan.Issues));
                var calls = 0;
                var result = OperationCreateWriter.Write(
                    plan, () => { calls++; return id; });
                Assert.That(result.Ok, Is.False, id + " must be rejected");
                Assert.That(calls, Is.EqualTo(1),
                    "a malformed draw fails on the spot, not by retry");
                Assert.That(
                    result.Errors.Exists(e => e.Contains(ErrorCodes.OW_OP_007)),
                    Is.False, id + " is a grammar error, not OW-OP-007");
                Assert.That(result.WrittenAssets, Is.Empty);
                Assert.IsFalse(File.Exists(Absolute(ProbeFile)),
                    "no file may be written for a malformed ID");
            }
        }

        // Commit-time check: a draw colliding with a registered ID makes
        // the menu draw again; a source that always collides ends in an
        // explicit error naming the collision, with no file written.
        [Test]
        public void Write_RegisteredIdCollision_FailsAfterBoundedDraws()
        {
            var snapshot = RegistryPublication.Shared.Snapshot;
            Assert.That(snapshot, Is.Not.Null,
                "the published Registry snapshot is required for the duplicate check");
            Assert.That(snapshot!.Find("proj.transform.reset", 1), Is.Not.Null,
                "the TestProject fixture must be registered for this test");
            var calls = 0;
            var plan = OperationCreatePlanner.Plan(NewRequest());
            Assert.That(plan.CanWrite, Is.True,
                "the plan must be writable before the ID check: " +
                string.Join("; ", plan.Issues));
            var result = OperationCreateWriter.Write(
                plan, () => { calls++; return "proj.transform.reset"; });
            Assert.That(result.Ok, Is.False);
            Assert.That(calls,
                Is.EqualTo(OperationIdGenerator.MaxGenerationAttempts),
                "colliding draws must stop after the bounded number of attempts");
            Assert.That(
                result.Errors.Exists(e => e.Contains("proj.transform.reset")),
                Is.True, "the error must name the collision, got: " +
                string.Join("; ", result.Errors));
            Assert.That(result.WrittenAssets, Is.Empty);
            Assert.IsFalse(File.Exists(Absolute(ProbeFile)),
                "no file may be written when every draw collides");
        }

        [Test]
        public void Plan_NonEditorFolder_ReportsOwUnity012WithBothChoices()
        {
            Directory.CreateDirectory(Absolute(RuntimeFolder));
            AssetDatabase.Refresh();
            var request = NewRequest();
            request.TargetFolder = RuntimeFolder;
            var plan = OperationCreatePlanner.Plan(request);
            Assert.That(plan.HasCode(ErrorCodes.OW_UNITY_012), Is.True);
            Assert.That(plan.FolderChoices,
                Is.EquivalentTo(new[]
                {
                    OperationCreateFolderChoice.CreateCustomAsmdef,
                    OperationCreateFolderChoice.UseDefaultFolder,
                }));
            Assert.That(plan.CanWrite, Is.False);
        }

        // F1 regression: the default-generation path must satisfy its own
        // OW-UNITY-012 check — the probe folder under an "Editor" segment
        // resolves to the predefined Editor assembly even when the
        // compilation pipeline cannot name a script that does not exist yet.
        [Test]
        public void Plan_DefaultFolder_IsEditorOnlyWithoutOwUnity012()
        {
            var plan = OperationCreatePlanner.Plan(NewRequest());
            Assert.That(plan.HasCode(ErrorCodes.OW_UNITY_012), Is.False,
                "unexpected issues: " + string.Join("; ", plan.Issues));
            Assert.That(plan.TargetAssemblyName,
                Is.EqualTo("Assembly-CSharp-Editor"));
        }

        // F2 regression: an existing Editor-only asmdef that does not
        // reference OpWeave.Core and OpWeave.UnityAdapter cannot compile
        // the generated template — the planner refuses with OW-UNITY-012
        // instead of writing a file that breaks the project's compilation.
        // The folder already carries an asmdef, so only the default-folder
        // choice is offered (a second same-folder asmdef is not valid).
        [Test]
        public void Plan_EditorOnlyAsmdefMissingTemplateReferences_ReportsOwUnity012()
        {
            var request = NewRequest();
            request.DisplayName = "Asmdef Probe";
            request.TargetFolder = "Assets/OpWeaveFixtures/Editor";
            var plan = OperationCreatePlanner.Plan(request);
            Assert.That(plan.TargetAssemblyName,
                Is.EqualTo("OpWeaveFixtures.Editor"));
            Assert.That(plan.HasCode(ErrorCodes.OW_UNITY_012), Is.True,
                "an Editor-only assembly without OpWeave.Core and " +
                "OpWeave.UnityAdapter references must not accept a " +
                "generated Operation");
            Assert.That(plan.CanWrite, Is.False);
            Assert.That(plan.FolderChoices,
                Is.EquivalentTo(new[]
                {
                    OperationCreateFolderChoice.UseDefaultFolder,
                }));
        }

        // The matching positive case: the §M.2-shaped Editor-only asmdef
        // that does carry both template references plans clean.
        [Test]
        public void Plan_EditorOnlyAsmdefWithTemplateReferences_IsAccepted()
        {
            var request = NewRequest();
            request.DisplayName = "Asmdef Probe";
            request.TargetFolder = "Assets/OpWeave/Editor/Operations/Transform";
            var plan = OperationCreatePlanner.Plan(request);
            Assert.That(plan.TargetAssemblyName,
                Is.EqualTo("OpWeave.Custom"));
            Assert.That(plan.HasErrors, Is.False,
                "unexpected issues: " + string.Join("; ", plan.Issues));
        }

        [Test]
        public void PlanForChoice_UseDefaultFolder_RetargetsToEditorOnly()
        {
            Directory.CreateDirectory(Absolute(RuntimeFolder));
            AssetDatabase.Refresh();
            var request = NewRequest();
            request.TargetFolder = RuntimeFolder;
            var plan = OperationCreatePlanner.PlanForChoice(
                request, OperationCreateFolderChoice.UseDefaultFolder);
            Assert.That(plan.HasCode(ErrorCodes.OW_UNITY_012), Is.False);
            Assert.That(plan.TargetFolder,
                Is.EqualTo(OperationCreatePlanner.DefaultFolder()));
            Assert.That(plan.HasErrors, Is.False,
                "unexpected issues: " + string.Join("; ", plan.Issues));
        }

        [Test]
        public void PlanForChoice_CreateCustomAsmdef_SchedulesAsmdefs()
        {
            Directory.CreateDirectory(Absolute(RuntimeFolder));
            AssetDatabase.Refresh();
            var request = NewRequest();
            request.TargetFolder = RuntimeFolder;
            var plan = OperationCreatePlanner.PlanForChoice(
                request, OperationCreateFolderChoice.CreateCustomAsmdef);
            Assert.That(plan.HasCode(ErrorCodes.OW_UNITY_012), Is.False);
            Assert.That(plan.OperationAsmdefPath,
                Is.EqualTo(RuntimeFolder + "/OpWeave.Custom.asmdef"));
            Assert.That(plan.OperationAsmdefName, Is.Not.Null.And.Not.Empty);
            Assert.That(plan.TestAsmdefPath,
                Is.EqualTo(RuntimeFolder + "/Tests/" +
                    plan.TestAsmdefName + ".asmdef"));
            Assert.That(plan.TestFilePath,
                Is.EqualTo(RuntimeFolder + "/Tests/" +
                    ProbeTestClassName + ".cs"));
            Assert.That(plan.HasErrors, Is.False,
                "unexpected issues: " + string.Join("; ", plan.Issues));
        }

        [Test]
        public void Plan_FileCollision_ReportsError()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Absolute(ProbeFile))!);
            File.WriteAllText(Absolute(ProbeFile), "// placeholder\n");
            var plan = OperationCreatePlanner.Plan(NewRequest());
            Assert.That(plan.HasErrors, Is.True);
            Assert.That(plan.CanWrite, Is.False);
        }

        [Test]
        public void Plan_ClassNameCollision_ReportsError()
        {
            var request = NewRequest();
            request.DisplayName = "Reset Transform";
            request.TargetFolder = "Assets/OpWeave/Editor/Operations/Transform";
            var plan = OperationCreatePlanner.Plan(request);
            Assert.That(plan.ClassName, Is.EqualTo("ResetTransformOperation"));
            Assert.That(plan.HasErrors, Is.True,
                "OpWeave.Custom.Transform.ResetTransformOperation already exists");
        }

        [Test]
        public void Plan_PortChecks_NameTypeAndDuplicates()
        {
            var request = NewRequest();
            request.Ports.Add(new OperationCreatePort
                { Name = "target", TypeId = "unity.GameObjectRef" });
            request.Ports.Add(new OperationCreatePort
                { Name = "target", TypeId = "unity.GameObjectRef" });
            request.Ports.Add(new OperationCreatePort
                { Name = "bad-name!", TypeId = "unity.GameObjectRef" });
            request.Ports.Add(new OperationCreatePort
                { Name = "missing", TypeId = "unity.NoSuchType" });
            var plan = OperationCreatePlanner.Plan(request);
            Assert.That(plan.HasErrors, Is.True);
        }

        [Test]
        public void Plan_UnknownEffect_ReportsError()
        {
            var request = NewRequest();
            request.Effects.Add("scene.explode");
            var plan = OperationCreatePlanner.Plan(request);
            Assert.That(plan.HasErrors, Is.True);
        }

        [Test]
        public void Render_GeneratedSources_CarryMatchingIdentityAndNoMarkers()
        {
            var request = NewRequest();
            request.Summary = "A generated probe.";
            request.DisplayNameJa = "メニュープローブ";
            request.Category = "Probes";
            request.Effects.Add(EffectNames.Read);
            request.Ports.Add(new OperationCreatePort
                { Name = "target", TypeId = "unity.GameObjectRef" });
            var operation = OperationTemplates.RenderOperation(
                request, ProbeNamespace, ProbeClassName, SampleGeneratedId);
            Assert.That(operation, Is.Not.Null);
            Assert.That(operation,
                Does.Contain("[OpWeaveOperation(\"" + SampleGeneratedId + "\", 1)]"));
            Assert.That(operation,
                Does.Contain(".Define(\"" + SampleGeneratedId + "\", 1)"));
            Assert.That(operation,
                Does.Contain("class " + ProbeClassName + " : OperationBase"));
            Assert.That(operation,
                Does.Contain("namespace " + ProbeNamespace));
            Assert.That(operation, Does.Contain(".Effects(\"read\")"));
            Assert.That(operation, Does.Contain(".Idempotency(Idempotency.Unsafe)"));
            Assert.That(operation,
                Does.Contain(".Input(\"target\", \"unity.GameObjectRef\", required: true)"));
            Assert.That(operation,
                Does.Contain("DisplayName = \"" + ProbeDisplayName + "\""));

            var tests = OperationTemplates.RenderTests(
                request, "OpWeave.Custom.Tests", ProbeTestClassName,
                SampleGeneratedId);
            Assert.That(tests, Is.Not.Null);
            Assert.That(tests,
                Does.Contain("OperationContract.AssertDescriptorValid(\"" +
                    SampleGeneratedId + "\")"));
            Assert.That(tests,
                Does.Contain("OperationContract.AssertIdempotency(\"" +
                    SampleGeneratedId + "\", inputs)"));
            Assert.That(tests,
                Does.Contain("OperationContract.AssertUndo(\"" +
                    SampleGeneratedId + "\", inputs)"));
        }

        // ── SPEC §M.4 test 2 (generate → compile → register → tests) ─────

        // local:T-W2.9-07:create-operation-uuid: generate an Operation with
        // defaults, compile, and run the generated tests across the reload.
        // The generated UUID ID is carried across the domain reload in
        // SessionState (it persists across reloads in the same Editor
        // session), and the generated source still holds it byte-for-byte.
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Write_Defaults_CompilesRegistersAndGeneratedTestPasses()
        {
            var write = WriteProbe();
            Assert.That(write.Ok, Is.True,
                "the generated write failed: " + string.Join("; ", write.Errors));
            var operationId = write.OperationId;
            Assert.That(operationId, Is.Not.Null);
            Assert.That(operationId, Does.Match(GeneratedIdPattern),
                "the committed ID must be the §A4 'proj.op_' + 32-hex form");
            Assert.That(
                File.ReadAllText(Absolute(ProbeFile)),
                Does.Contain("\"" + operationId + "\""),
                "the generated file must carry the generated ID");
            SessionState.SetString(GeneratedIdSessionKey, operationId!);

            yield return new BoundedScriptReload(
                nameof(Write_Defaults_CompilesRegistersAndGeneratedTestPasses),
                WaitTimeoutSec);
            yield return WaitWhileBusy(
                nameof(Write_Defaults_CompilesRegistersAndGeneratedTestPasses) +
                    ".DomainReload",
                () => DomainSentinelAlive,
                WaitTimeoutSec,
                "the domain sentinel — the generated write's " +
                    "domain reload");
            Assert.IsFalse(DomainSentinelAlive,
                "writing the generated files did not produce a domain reload");

            // UTF does not restore enumerator locals across the reload:
            // operationId is read back only from SessionState, where it
            // was persisted before the reload.
            var persistedId = SessionState.GetString(
                GeneratedIdSessionKey, string.Empty);
            Assert.That(persistedId, Does.Match(GeneratedIdPattern),
                "the generated ID must be unchanged by the domain reload");
            Assert.That(
                File.ReadAllText(Absolute(ProbeFile)),
                Does.Contain("\"" + persistedId + "\""),
                "the generated file must still carry the same ID after reload");

            s_pollOperationId = persistedId;
            s_pollResult = null;
            yield return WaitWhileBusy(
                nameof(Write_Defaults_CompilesRegistersAndGeneratedTestPasses) +
                    ".Registry",
                PollResultMissing,
                WaitTimeoutSec,
                "the registration of '" + persistedId + "'");
            Assert.That(s_pollResult, Is.Not.Null,
                "'" + persistedId + "' was not registered after the reload");

            var check = CheckAfterReload(s_pollResult!, persistedId);
            Assert.That(check.Ok, Is.True, check.Failure);
        }

        // Phase 1: plan + write + import, then arm the reload sentinel.
        private static OperationCreateWriter.Result WriteProbe()
        {
            ArmDomainSentinel();
            var request = NewRequest();
            var plan = OperationCreatePlanner.Plan(request);
            if (!plan.CanWrite)
            {
                var refused = new OperationCreateWriter.Result();
                refused.Errors.Add(
                    "the plan was refused: " + string.Join("; ", plan.Issues));
                return refused;
            }
            var result = OperationCreateWriter.Write(plan);
            if (result.Ok)
            {
                Assert.That(result.WrittenAssets, Does.Contain(ProbeFile));
                Assert.That(result.WrittenAssets, Does.Contain(TestProbeFile));
            }
            return result;
        }

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;
            internal static PhaseResult Pass() => new PhaseResult { Ok = true };
            internal static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        // Phase 2 (new domain): the generated attribute identity equals the
        // generated Descriptor identity; the operation registered without
        // OW-OP-003; and the generated contract test passes.
        private static PhaseResult CheckAfterReload(
            RegisteredOperation found, string operationId)
        {
            try
            {
                Assert.That(found.Origin, Is.EqualTo(OperationOrigin.Custom));
                Assert.That(found.Descriptor.Id, Is.EqualTo(operationId));
                Assert.That(found.Descriptor.Version, Is.EqualTo(1));

                var snapshot = RegistryPublication.Shared.Snapshot;
                if (snapshot is not null)
                {
                    foreach (var failure in snapshot.CandidateFailures)
                    {
                        Assert.That(failure.CandidateId,
                            Is.Not.EqualTo(operationId),
                            "the generated Operation was rejected: " +
                            string.Join(", ", failure.Diagnostics));
                    }
                }

                var type = FindType(ProbeNamespace + "." + ProbeClassName);
                Assert.That(type, Is.Not.Null,
                    "the generated class did not compile into the domain");
                var declared = OperationEligibility.DeclaredIdentityOf(type!);
                Assert.That(declared, Is.Not.Null);
                Assert.That(declared!.Id, Is.EqualTo(found.Descriptor.Id),
                    "attribute ID must equal the Descriptor ID");
                Assert.That(declared.Version, Is.EqualTo(found.Descriptor.Version),
                    "attribute version must equal the Descriptor version");

                var testType = FindType(
                    "OpWeave.Custom.Tests." + ProbeTestClassName);
                Assert.That(testType, Is.Not.Null,
                    "the generated test class did not compile");
                RunGeneratedTest(testType!);
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail(e.ToString());
            }
        }

        private static Type? FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName);
                if (type is not null)
                {
                    return type;
                }
            }
            return null;
        }

        // Invokes the generated [TearDown] + [Test] methods — the generated
        // tests really run, including their NUnit assertions.
        private static void RunGeneratedTest(Type testType)
        {
            var instance = Activator.CreateInstance(testType);
            var tearDown = testType.GetMethod("TearDown");
            var contract = testType.GetMethod("Contract");
            Assert.That(contract, Is.Not.Null);
            try
            {
                contract!.Invoke(instance, null);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
            finally
            {
                tearDown?.Invoke(instance, null);
            }
        }
    }
}
