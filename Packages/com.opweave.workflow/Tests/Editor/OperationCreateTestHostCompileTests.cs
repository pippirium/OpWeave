#nullable enable

// T-W2.9-05 [Edit] test: the end-to-end half of
// local:T-W2.9-05:test-host-nunit — the SPEC §M.4 acceptance criterion
// "the generated test compiles in the host that is chosen". The probe
// request targets the otherwise-qualifying
// OpWeaveFixtures.TestHostNoNUnit asmdef's folder (Editor-only,
// UNITY_INCLUDE_TESTS, OpWeave.Testing and every template reference —
// but overrideReferences without nunit.framework.dll, so a generated
// contract test under it cannot compile). The production placement path
// the Create Operation menu runs after its dialog —
// OperationCreatePlanner.Plan then OperationCreateWriter.Write — must
// put the generated test in the first qualifying host
// (OpWeave.Custom.Tests), and after a real domain reload, crossed
// through the fixture's own bounded reload instruction, the
// generated class must exist in the loaded host assembly
// with no compiler error reported for it. The CompilerMessage lines are
// appended to a file under Library/ inside the pipeline callback —
// persisted before the domain unloads; a static field would die with it.
using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.9-05:test-host-nunit — the generated contract test
    /// written through the production Create path compiles inside the
    /// host the planner chose, never under the unqualified
    /// TestHostNoNUnit asmdef.
    /// </summary>
    internal sealed class OperationCreateTestHostCompileTests
    {
        private const string NoNUnitFolder =
            "Assets/OpWeaveFixtures/TestHostNoNUnit";
        private const string NoNUnitAssembly = "OpWeaveFixtures.TestHostNoNUnit";
        private const string HostAssembly = "OpWeave.Custom.Tests";
        private const string HostTestNamespace = "OpWeave.Custom.Tests";

        private const string ProbeDisplayName = "Test Host Compile Probe";
        private const string ProbeClassName = "TestHostCompileProbeOperation";
        private const string ProbeTestClassName =
            "TestHostCompileProbeOperationTests";

        private const string ProbeOperationFile =
            NoNUnitFolder + "/" + ProbeClassName + ".cs";
        private const string ProbeTestFile =
            "Assets/OpWeave/Editor/Operations/Transform/Tests/" +
            ProbeTestClassName + ".cs";
        // The placement the unqualified-host planner would compute, plus
        // the §M.2 artifacts a "no qualifying host" plan would schedule —
        // cleanup covers all of them so a failed run still leaves the
        // next fixture clean.
        private const string MisplacedTestFile =
            NoNUnitFolder + "/" + ProbeTestClassName + ".cs";
        private const string ScheduledOperationAsmdef =
            NoNUnitFolder + "/OpWeave.Custom.asmdef";
        private const string ScheduledTestsFolder = NoNUnitFolder + "/Tests";

        private const double SettleDeadlineSeconds = 240.0;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string Absolute(string assetRelative) =>
            Path.Combine(
                ProjectRoot,
                assetRelative.Replace('/', Path.DirectorySeparatorChar));

        // The compile-error capture: one "assembly|file|message" line per
        // CompilerMessageType.Error, appended inside the pipeline
        // callback so it is on disk before the domain reload.
        private static string CaptureDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveTestHostCompileProbe");
        private static string CaptureFile =>
            Path.Combine(CaptureDir, "compile-errors.txt");

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

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

        private static bool FolderOrMetaExists(string assetPath) =>
            AssetDatabase.IsValidFolder(assetPath) ||
            Directory.Exists(Absolute(assetPath)) ||
            File.Exists(Absolute(assetPath) + ".meta");

        // True only when Unity already imported the asset — its .meta
        // file is the deterministic marker. Only removing an imported
        // script or asmdef schedules a recompile; deleting a never-
        // imported placeholder does not.
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

        // Deepest first: the generated files under their planned,
        // misplaced and scheduled paths, the stray asmdef, the Tests/
        // subtree, then the Library capture dir. Returns true only when
        // the cleanup deleted a script or asmdef Unity had already
        // imported — the sole case that queues a recompile + reload.
        private static bool CleanupProbeArtifacts()
        {
            var dirty = false;
            var scriptPending = false;
            if (File.Exists(Absolute(ProbeOperationFile)))
            {
                scriptPending |= IsImportedAsset(ProbeOperationFile);
                DeleteAssetPath(ProbeOperationFile);
                dirty = true;
            }
            if (File.Exists(Absolute(ProbeTestFile)))
            {
                scriptPending |= IsImportedAsset(ProbeTestFile);
                DeleteAssetPath(ProbeTestFile);
                dirty = true;
            }
            if (File.Exists(Absolute(MisplacedTestFile)))
            {
                scriptPending |= IsImportedAsset(MisplacedTestFile);
                DeleteAssetPath(MisplacedTestFile);
                dirty = true;
            }
            if (File.Exists(Absolute(ScheduledOperationAsmdef)))
            {
                scriptPending |= IsImportedAsset(ScheduledOperationAsmdef);
                DeleteAssetPath(ScheduledOperationAsmdef);
                dirty = true;
            }
            if (FolderOrMetaExists(ScheduledTestsFolder))
            {
                scriptPending |= FolderHasImportedScript(ScheduledTestsFolder);
                DeleteFolderAsset(ScheduledTestsFolder);
                dirty = true;
            }
            if (Directory.Exists(CaptureDir))
            {
                Directory.Delete(CaptureDir, recursive: true);
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            return scriptPending;
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
        // fixture can pin — settles through WaitForEditorIdle, a
        // bounded plain-frame wait that fails naming the phase and
        // the watched state rather than waiting forever.
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
            string phase, Func<bool> busy, double timeoutSec, string waitedOn)
        {
            var deadline = EditorApplication.timeSinceStartup + timeoutSec;
            while (busy())
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "OperationCreateTestHostCompileTests." + phase +
                        " timed out after " + timeoutSec +
                        " s waiting for " + waitedOn);
                }
                yield return null;
            }
        }

        private static IEnumerator WaitForEditorIdle(string phase) =>
            WaitWhileBusy(
                phase,
                () => EditorApplication.isCompiling ||
                    EditorApplication.isUpdating,
                SettleDeadlineSeconds,
                EditorBusyState + " to clear");

        // RecompileScripts.Perform with deadlines, run by the EditMode
        // runner through the IEditModeTestYieldInstruction interface —
        // so ExpectDomainReload arms UTF's reload bookkeeping exactly as
        // with UTF's own instruction, and the domain reload itself still
        // ends the instruction.
        private sealed class BoundedScriptReload : IEditModeTestYieldInstruction
        {
            private readonly string m_phase;
            private readonly double m_timeoutSec;

            internal BoundedScriptReload(string phase, double timeoutSec)
            {
                m_phase = phase;
                m_timeoutSec = timeoutSec;
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

                AssetDatabase.Refresh();

                // Prove the queued compile really started before
                // crossing — the same precondition RecompileScripts
                // asserts up front, except Refresh returns before
                // isCompiling goes true, so it gets bounded frames from
                // the same deadline. On expiry nothing is unlocked yet,
                // so no LockReloadAssemblies is needed.
                while (!EditorApplication.isCompiling)
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        Assert.Fail(
                            "OperationCreateTestHostCompileTests." +
                            m_phase + " timed out after " + m_timeoutSec +
                            " s: no script compilation started");
                    }
                    yield return null;
                }

                EditorApplication.UnlockReloadAssemblies();

                while (EditorApplication.isCompiling)
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        EditorApplication.LockReloadAssemblies();
                        Assert.Fail(
                            "OperationCreateTestHostCompileTests." +
                            m_phase + " timed out after " + m_timeoutSec +
                            " s: EditorApplication.isCompiling did " +
                            "not clear");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "OperationCreateTestHostCompileTests." + m_phase +
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
                    nameof(UnitySetUp), SettleDeadlineSeconds);
            }
            yield return WaitForEditorIdle(nameof(UnitySetUp));
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var scriptPending = CleanupProbeArtifacts();
            if (scriptPending)
            {
                yield return new BoundedScriptReload(
                    nameof(UnityTearDown), SettleDeadlineSeconds);
            }
            yield return WaitForEditorIdle(nameof(UnityTearDown));
        }

        // local:T-W2.9-05:test-host-nunit acceptance: target the
        // TestHostNoNUnit folder, run the production write, and prove the
        // generated contract test compiles inside the chosen host.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Write_GeneratedTestCompilesInChosenHost()
        {
            var request = new OperationCreateRequest
            {
                DisplayName = ProbeDisplayName,
                TargetFolder = NoNUnitFolder,
            };
            var plan = OperationCreatePlanner.Plan(request);
            Assert.That(plan.HasErrors, Is.False,
                "unexpected issues: " + string.Join("; ", plan.Issues));
            Assert.That(plan.CanWrite, Is.True);
            Assert.That(plan.TestFilePath, Is.EqualTo(ProbeTestFile));
            Assert.That(plan.TestNamespace, Is.EqualTo(HostTestNamespace));

            // The same internal write the menu's Create button runs after
            // its dialog (OperationCreateWindow.Write → the writer).
            var result = OperationCreateWriter.Write(plan);
            Assert.That(result.Ok, Is.True,
                "the generated write failed: " +
                string.Join("; ", result.Errors));
            Assert.That(result.OperationId,
                Does.Match("\\Aproj\\.op_[0-9a-f]{32}\\z"),
                "the committed ID must be the §A4 generated form");
            Assert.That(
                File.ReadAllText(Absolute(ProbeOperationFile)),
                Does.Contain("\"" + result.OperationId + "\""),
                "the generated Operation file must carry the generated ID");
            Assert.That(
                File.ReadAllText(Absolute(ProbeTestFile)),
                Does.Contain("\"" + result.OperationId + "\""),
                "the generated test must carry the same generated ID");
            Assert.That(result.WrittenAssets, Does.Contain(ProbeOperationFile));
            Assert.That(result.WrittenAssets, Does.Contain(ProbeTestFile));
            Assert.IsTrue(File.Exists(Absolute(ProbeTestFile)),
                "the generated test file must be written under the " +
                "chosen host");
            foreach (var written in result.WrittenAssets)
            {
                if (written.StartsWith(
                    NoNUnitFolder + "/", StringComparison.Ordinal))
                {
                    Assert.That(written, Is.EqualTo(ProbeOperationFile),
                        "only the probe operation may be written under " +
                        "the TestHostNoNUnit folder — never the " +
                        "generated test");
                }
            }
            Assert.IsFalse(File.Exists(Absolute(MisplacedTestFile)),
                "the generated test must not be written under the " +
                "unqualified host");

            Directory.CreateDirectory(CaptureDir);
            CompilationPipeline.assemblyCompilationFinished +=
                OnAssemblyCompilationFinished;
            ArmDomainSentinel();
            yield return new BoundedScriptReload(
                nameof(Write_GeneratedTestCompilesInChosenHost),
                SettleDeadlineSeconds);
            yield return WaitWhileBusy(
                nameof(Write_GeneratedTestCompilesInChosenHost) +
                    ".DomainReload",
                () => DomainSentinelAlive,
                SettleDeadlineSeconds,
                "the domain sentinel — the generated write's " +
                    "domain reload");
            Assert.IsFalse(DomainSentinelAlive,
                "writing the generated files did not produce a domain reload");
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the project must compile with the generated test in " +
                "its chosen host");

            var errors = File.Exists(CaptureFile)
                ? File.ReadAllLines(CaptureFile)
                : Array.Empty<string>();
            foreach (var line in errors)
            {
                Assert.IsFalse(
                    line.StartsWith(
                        HostAssembly + "|", StringComparison.Ordinal),
                    "a compiler error was reported for the chosen host " +
                    "assembly: " + line);
                Assert.IsFalse(
                    line.StartsWith(
                        NoNUnitAssembly + "|", StringComparison.Ordinal),
                    "a compiler error was reported for the probe " +
                    "operation's fixture assembly: " + line);
            }

            var testType = FindType(
                HostTestNamespace + "." + ProbeTestClassName);
            Assert.That(testType, Is.Not.Null,
                "the generated test class did not compile into the domain");
            Assert.That(testType!.Assembly.GetName().Name,
                Is.EqualTo(HostAssembly),
                "the generated test class must live in the chosen host " +
                "assembly");
        }

        // The CompilationPipeline.assemblyCompilationFinished handler:
        // appends each error inside the callback so it is persisted
        // before the domain reload drops the subscription and every
        // static.
        private static void OnAssemblyCompilationFinished(
            string assemblyPath, CompilerMessage[] messages)
        {
            if (messages is null)
            {
                return;
            }
            var assembly = Path.GetFileNameWithoutExtension(assemblyPath) ??
                assemblyPath;
            foreach (var message in messages)
            {
                if (message.type != CompilerMessageType.Error)
                {
                    continue;
                }
                Directory.CreateDirectory(CaptureDir);
                File.AppendAllText(
                    CaptureFile,
                    assembly + "|" + message.file + "|" +
                    (message.message ?? string.Empty).Replace('\n', ' ') +
                    "\n");
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
    }
}
