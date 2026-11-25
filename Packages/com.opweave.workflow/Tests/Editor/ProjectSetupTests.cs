#nullable enable

// T-W2.9-03 [Edit] tests: local:T-W2.9-03:setup-project — SPEC §M.3 test.
// Setup runs on an empty project (a scratch project root under Library/ —
// Assets/OpWeave is already populated in TestProject, so a scratch root is the
// empty project): every layout item is created on the first run, the second
// run skips everything and changes nothing, and existing files are never
// overwritten. The "created items compile" half cannot place a second
// OpWeave.Custom asmdef inside this project (Unity errors on a duplicate
// assembly name — the committed fixture already defines it), so the
// [UnityTest] writes the generated asmdef JSON under Assets/ with only the
// assembly-name string swapped for a unique probe name, lets a real
// recompile + domain reload prove the generated shape compiles, and then
// verifies the second run is still fully skipped in the new domain.
// T-W2.4-10: the pre-reload phase records a per-Editor-session nonce in
// Library/ mirrored in SessionState, setup and teardown never yield a
// reload emitter (the queued cleanup compile drains at the test body's
// own boundary), and the boundary loop re-drives a phase-1 a stale
// resume skipped — the ChangeSetDomainReloadTests shape.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class ProjectSetupTests
    {
        // The fake project root the setup runs against: a Library/ subtree that
        // starts empty, so the first run has every item to create.
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ScratchProjectRoot =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveSetupProbe", "EmptyProject");

        private const string ProbeAssetsDir = "Assets/OpWeaveSetupProbe";
        private const string ProbeCustomDir = ProbeAssetsDir + "/Custom";
        private const string ProbeTestsDir = ProbeAssetsDir + "/Tests";
        private const string ProbeCustomAssembly = "OpWeave.SetupProbe.Custom";
        private const string ProbeTestsAssembly = "OpWeave.SetupProbe.Custom.Tests";

        private static readonly string[] ExpectedFolders =
        {
            "Assets/OpWeave",
            "Assets/OpWeave/Workflows",
            "Assets/OpWeave/Editor",
            "Assets/OpWeave/Editor/Operations",
            "Assets/OpWeave/Editor/Operations/Transform",
            "Assets/OpWeave/Editor/Operations/Animation",
            "Assets/OpWeave/Editor/Operations/Material",
            "Assets/OpWeave/Editor/Operations/Custom",
            "Assets/OpWeave/Editor/Tests",
        };

        private static readonly string[] ExpectedFiles =
        {
            "Assets/OpWeave/Workflows/sample.opweave.json",
            "Assets/OpWeave/Editor/Operations/OpWeave.Custom.asmdef",
            "Assets/OpWeave/Editor/Tests/OpWeave.Custom.Tests.asmdef",
            "ProjectSettings/OpWeaveSettings.json",
        };

        private static string Disk(string projectRelative) =>
            Path.Combine(
                ScratchProjectRoot,
                projectRelative.Replace('/', Path.DirectorySeparatorChar));

        // Scratch cleanup lives in the [UnitySetUp]/[UnityTearDown] pair
        // below, never in plain [SetUp]/[TearDown]: UTF does not persist
        // plain before/after state across a domain reload, so a plain
        // [SetUp] would run a second time when the [UnityTest] resumes
        // after its RecompileScripts reload and delete the scratch tree the
        // post-reload assertions read.
        private static void CleanScratch()
        {
            if (Directory.Exists(ScratchProjectRoot))
            {
                Directory.Delete(ScratchProjectRoot, recursive: true);
            }
        }

        private static void AssertEveryItem(
            OpWeaveSetupResult result, OpWeaveSetupItemState state)
        {
            Assert.That(result.Failed, Is.Empty,
                "failed items: " + string.Join(
                    "; ", result.Failed.Select(i => i.Path + " (" + i.Error + ")")));
            var byPath = result.Items.ToDictionary(i => i.Path, i => i);
            foreach (var folder in ExpectedFolders)
            {
                Assert.That(byPath.ContainsKey(folder), Is.True,
                    "missing layout item " + folder);
                Assert.That(byPath[folder].Kind, Is.EqualTo(OpWeaveSetupItemKind.Folder));
                Assert.That(byPath[folder].State, Is.EqualTo(state),
                    folder + " must be " + state);
            }
            foreach (var file in ExpectedFiles)
            {
                Assert.That(byPath.ContainsKey(file), Is.True,
                    "missing layout item " + file);
                Assert.That(byPath[file].Kind, Is.EqualTo(OpWeaveSetupItemKind.File));
                Assert.That(byPath[file].State, Is.EqualTo(state),
                    file + " must be " + state);
            }
            Assert.That(result.Items.Count, Is.EqualTo(
                ExpectedFolders.Length + ExpectedFiles.Length),
                "the result must cover exactly the SPEC §M.3 item list");
        }

        private static Dictionary<string, string> TreeSnapshot()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(
                ScratchProjectRoot, "*", SearchOption.AllDirectories))
            {
                map[file.Substring(ScratchProjectRoot.Length + 1)] =
                    Convert.ToBase64String(File.ReadAllBytes(file));
            }
            return map;
        }

        // ── live-build drain (T-W2.4-13) ───────────────────────────────
        //
        // GeneratedSampleWorkflow_ParsesAndValidates reads the production
        // §C3 stage-1 seam — the real Editor compile state — so a compile
        // a predecessor fixture left queued behind the runner's reload
        // lock (isCompiling still false, IsScriptReloadRequested already
        // armed) hands its RegistryBuildHost.Rebuild a stale stage 1: the
        // Issue #621 failure. The drain below is the
        // At01AgentStepsTests DrainEditorState idiom (T-W2.4-07) — but
        // relayed INSIDE the test body, never from [UnitySetUp] /
        // [UnityTearDown]: this fixture's setup/teardown must never yield
        // an ExpectDomainReload instruction because UTF's
        // PrepareForDomainReload then records a stale PC on a different
        // test's method enumerator and the resume jumps the next
        // [UnityTest] past phase-1 (the T-W2.4-10 note below).
        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // Bound on re-drain/rebuild legs after a build reads a stale or
        // still-armed stage 1 (T-W2.4-15); each leg's drain stays bounded
        // by WaitTimeoutSec, so the loop can never wait indefinitely.
        private const int MaxRebuildLegs = 3;

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the drain
        // reaches it through reflection (the At01AgentStepsTests probe,
        // T-W2.4-07). A reload already armed behind the runner's reload
        // lock surfaces here while isCompiling is false — the deferred
        // state an isCompiling-only check misses.
        private const string ScriptReloadProbeType =
            "UnityEditorInternal.InternalEditorUtility";
        private const string ScriptReloadProbeMethod =
            "IsScriptReloadRequested";

        private static readonly MethodInfo? s_scriptReloadRequested =
            typeof(EditorApplication).Assembly
                .GetType(ScriptReloadProbeType)
                ?.GetMethod(
                    ScriptReloadProbeMethod,
                    BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

        // Non-null and shaped as OneTimeSetUp proved — the fixture does
        // not run at all on a Unity version whose probe is absent.
        private static bool ScriptReloadRequested() =>
            (bool)s_scriptReloadRequested!.Invoke(null, null)!;

        // The state a queued compile/reload reports while the runner's
        // lock holds it: a running script compile always ends in a reload
        // queued behind the lock, and a requested reload is already
        // pending — either is one the drain must cross, never ride into
        // a live build below.
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling ||
            CompilationAssembliesPending();

        // The queued-but-unstarted/mid-transition window the two flags
        // cannot see (T-W2.4-15): a predecessor can arm a compile that has
        // not yet raised isCompiling or IsScriptReloadRequested, while the
        // compilation pipeline already refuses to answer — its
        // GetAssemblies result going null or its query throwing is the
        // only observable the drain→build gap leaves. Treat an unanswered
        // pipeline as pending: the drain keeps waiting until the pipeline
        // serves the query the registry build and the compiler's plan
        // stage are about to run.
        private static bool CompilationAssembliesPending()
        {
            try
            {
                return CompilationPipeline.GetAssemblies(AssembliesType.Editor) is null ||
                    CompilationPipeline.GetAssemblies(AssembliesType.Player) is null;
            }
            catch (Exception)
            {
                return true;
            }
        }

        // The pending probe the drain trusts is only as strong as its
        // shape: prove it once up front (the At01AgentStepsTests pattern,
        // T-W2.4-07) so the fixture fails here — before any test —
        // instead of silently degrading to an isCompiling-only check
        // that misses a requested reload armed behind the lock.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var probe = s_scriptReloadRequested;
            if (probe is null ||
                !probe.IsStatic ||
                probe.GetParameters().Length != 0 ||
                probe.ReturnType != typeof(bool))
            {
                Assert.Fail(
                    "the domain-reload pending probe is unavailable on " +
                    "this Unity version — the drain cannot prove a " +
                    "deferred reload is drained");
            }
        }

        // The contract the live-build test needs before its rebuild: no
        // pending script reload, no compile or import in flight and a
        // clean compile. A pending compile or requested reload is crossed
        // only at DrainedScriptReload — the bounded instruction that
        // unlocks once the pending state is proven — and everything else
        // settles on bounded plain frames. A queued compile surfaces as
        // isCompiling a frame or two after Refresh returns, so the wait
        // only trusts a sustained quiet spell (the QuietSettle
        // convention): quiet flags reset on every busy frame and a
        // periodic Refresh flushes the import queue so a queued compile
        // surfaces inside the window, never behind it. The instruction
        // the nested enumerator produces is yielded DIRECTLY from the
        // test body's own top level through the caller's MoveNext relay —
        // a nested yield would never reach Perform — and the whole
        // sequence is idempotent: a crossed reload resumes the caller's
        // enumerator at its yield with every local reset, so the caller
        // rebuilds the drain when it arrives null rather than relaying a
        // dead reference.
        private static IEnumerator DrainEditorState(string phase)
        {
            AssetDatabase.Refresh();
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var nextRefreshAt = double.NegativeInfinity;
            var quietAt = -1.0;
            for (;;)
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "ProjectSetupTests." + phase +
                        " timed out after " + WaitTimeoutSec +
                        " s draining the armed editor state " +
                        "(scriptReloadRequested: " + ScriptReloadRequested() +
                        ", isCompiling: " + EditorApplication.isCompiling +
                        ", isUpdating: " + EditorApplication.isUpdating + ")");
                }
                if (ReloadPending())
                {
                    // A running or requested compile ends in a reload
                    // queued behind the runner's lock — cross it at the
                    // bounded sanctioned boundary, never hand it on.
                    quietAt = -1.0;
                    yield return new DrainedScriptReload(
                        phase, WaitTimeoutSec);
                    continue;
                }
                if (EditorApplication.isUpdating)
                {
                    // An import still in flight may surface a compile —
                    // no quiet counts while it runs.
                    quietAt = -1.0;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - quietAt >=
                         QuietSettleSec)
                {
                    break;
                }
                if (EditorApplication.timeSinceStartup >= nextRefreshAt)
                {
                    AssetDatabase.Refresh();
                    nextRefreshAt = EditorApplication.timeSinceStartup + 1.0;
                }
                yield return null;
            }
            AssertDrainedEditorState(phase);
        }

        // The terminal clean-state proof: the project compiled clean, no
        // compile in flight and no script reload still armed behind the
        // runner's lock.
        private static void AssertDrainedEditorState(string phase)
        {
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "ProjectSetupTests." + phase +
                ": the project did not compile clean");
            Assert.IsFalse(EditorApplication.isCompiling,
                "ProjectSetupTests." + phase +
                ": a script compile is still running");
            Assert.IsFalse(ScriptReloadRequested(),
                "ProjectSetupTests." + phase +
                ": a script reload is still pending");
            Assert.IsFalse(CompilationAssembliesPending(),
                "ProjectSetupTests." + phase +
                ": the compilation pipeline cannot answer its assembly query");
        }

        // The bounded reload-crossing instruction — the
        // WaitForDomainReload.Perform wait with a deadline, run by UTF
        // through the IEditModeTestYieldInstruction interface so
        // ExpectDomainReload arms the runner's reload bookkeeping
        // exactly as with UTF's own instructions. It is yielded only
        // after ReloadPending() proved a real reload is armed, so the
        // unlock below always meets a queued reload and can never strand
        // the lock released on an idle Editor — the failure mode of the
        // unconditional RecompileScripts(false) unlock the drain idiom
        // replaces. The one path where no reload can follow — a failed
        // compile — re-locks before failing.
        private sealed class DrainedScriptReload : IEditModeTestYieldInstruction
        {
            private readonly string m_phase;
            private readonly double m_timeoutSec;

            internal DrainedScriptReload(string phase, double timeoutSec)
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
                EditorApplication.UnlockReloadAssemblies();

                // Wait out the armed state as WaitForDomainReload.Perform
                // does — while a script reload stays requested or a
                // compile runs — but bounded: the reload itself kills
                // the domain mid-wait, so a deadline expiry can only
                // mean the proven pending state never crossed.
                while (ReloadPending())
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        EditorApplication.LockReloadAssemblies();
                        Assert.Fail(
                            "ProjectSetupTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "ProjectSetupTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // ── First run on an empty project creates the whole §M.2 layout ──

        [Test]
        public void Run_EmptyProject_CreatesEntireSpecLayout()
        {
            Assert.IsFalse(OpWeaveProjectSetup.IsSetupComplete(ScratchProjectRoot),
                "a fresh scratch root must not look set up");
            var result = OpWeaveProjectSetup.Run(ScratchProjectRoot);
            AssertEveryItem(result, OpWeaveSetupItemState.Created);
            foreach (var folder in ExpectedFolders)
            {
                Assert.IsTrue(Directory.Exists(Disk(folder)),
                    folder + " was not created");
            }
            foreach (var file in ExpectedFiles)
            {
                Assert.IsTrue(File.Exists(Disk(file)),
                    file + " was not created");
            }
            Assert.IsTrue(OpWeaveProjectSetup.IsSetupComplete(ScratchProjectRoot),
                "a finished setup must report complete");
        }

        // ── Second run: everything skipped, byte-identical (SPEC §M.3 item 3) ──

        [Test]
        public void Run_SecondRun_SkipsEverythingAndChangesNothing()
        {
            OpWeaveProjectSetup.Run(ScratchProjectRoot);
            var before = TreeSnapshot();
            var second = OpWeaveProjectSetup.Run(ScratchProjectRoot);
            AssertEveryItem(second, OpWeaveSetupItemState.Skipped);
            var after = TreeSnapshot();
            Assert.That(after, Is.EqualTo(before),
                "the second run must leave the tree byte-identical");
        }

        // ── Existing content is never overwritten ──

        [Test]
        public void Run_ExistingContent_IsNeverOverwritten()
        {
            var sample = "Assets/OpWeave/Workflows/sample.opweave.json";
            var operations = "Assets/OpWeave/Editor/Operations";
            Directory.CreateDirectory(Disk(operations));
            const string sentinel = "{ \"kept\": true }\n";
            Directory.CreateDirectory(Disk("Assets/OpWeave/Workflows"));
            File.WriteAllText(Disk(sample), sentinel);

            var result = OpWeaveProjectSetup.Run(ScratchProjectRoot);
            var byPath = result.Items.ToDictionary(i => i.Path, i => i);
            Assert.That(byPath[sample].State, Is.EqualTo(OpWeaveSetupItemState.Skipped));
            Assert.That(byPath[operations].State, Is.EqualTo(OpWeaveSetupItemState.Skipped));
            Assert.AreEqual(sentinel, File.ReadAllText(Disk(sample)),
                "the pre-existing sample workflow must be left untouched");
            Assert.IsTrue(File.Exists(Disk(
                "Assets/OpWeave/Editor/Operations/OpWeave.Custom.asmdef")),
                "the rest of the layout must still be created around it");
        }

        // ── A destination created in the check/create gap is skipped, never
        //    truncated (SPEC §M.3 item 3) ──

        [Test]
        public void Run_DestinationCreatedInCheckGap_IsSkippedAndUntouched()
        {
            // The seam fires between the existence check and the atomic
            // create: the "racer" files it plants are exactly what a
            // concurrent writer could leave there.
            const string sentinel = "{ \"racer\": true }\n";
            var raced = new HashSet<string>(StringComparer.Ordinal)
            {
                Disk("Assets/OpWeave/Workflows/sample.opweave.json"),
                Disk("ProjectSettings/OpWeaveSettings.json"),
            };
            OpWeaveProjectSetup.CreateFileProbe = absolute =>
            {
                if (raced.Contains(absolute))
                {
                    File.WriteAllText(absolute, sentinel);
                }
            };
            try
            {
                var result = OpWeaveProjectSetup.Run(ScratchProjectRoot);
                Assert.That(result.Failed, Is.Empty,
                    "failed items: " + string.Join(
                        "; ", result.Failed.Select(i => i.Path + " (" + i.Error + ")")));
                var byPath = result.Items.ToDictionary(i => i.Path, i => i);
                Assert.That(
                    byPath["Assets/OpWeave/Workflows/sample.opweave.json"].State,
                    Is.EqualTo(OpWeaveSetupItemState.Skipped),
                    "a raced destination must be skipped, not overwritten");
                Assert.That(
                    byPath["ProjectSettings/OpWeaveSettings.json"].State,
                    Is.EqualTo(OpWeaveSetupItemState.Skipped),
                    "the settings file takes the same create-if-absent path");
                Assert.AreEqual(sentinel, File.ReadAllText(
                    Disk("Assets/OpWeave/Workflows/sample.opweave.json")),
                    "the racer's bytes must survive byte-identical");
                Assert.AreEqual(sentinel, File.ReadAllText(
                    Disk("ProjectSettings/OpWeaveSettings.json")),
                    "the settings destination must keep the racer's bytes too");
            }
            finally
            {
                OpWeaveProjectSetup.CreateFileProbe = null;
            }
        }

        // ── T-W5.3-03: the sample's create goes through the JSON save
        //    coordinator — a file planted before the publish move is never
        //    overwritten (SPEC §H7 item 3, §M.3 item 3) ──

        [Test]
        public void ProjectSetup_SampleCreate_PlantedFileNeverOverwritten()
        {
            // The probe plants the racer in the check/publish gap the
            // coordinator's create-only move closes: the planted bytes must
            // survive byte-identical, and the item reports Skipped.
            var sample = "Assets/OpWeave/Workflows/sample.opweave.json";
            var sampleDisk = Disk(sample);
            Directory.CreateDirectory(Path.GetDirectoryName(sampleDisk)!);
            const string sentinel = "{ \"planted\": true }\n";
            OpWeaveProjectSetup.CreateFileProbe = absolute =>
            {
                if (string.Equals(absolute, sampleDisk, StringComparison.Ordinal))
                {
                    File.WriteAllText(absolute, sentinel);
                }
            };
            try
            {
                var result = OpWeaveProjectSetup.Run(ScratchProjectRoot);
                var byPath = result.Items.ToDictionary(i => i.Path, i => i);
                Assert.That(byPath[sample].State,
                    Is.EqualTo(OpWeaveSetupItemState.Skipped),
                    "a file planted before the move is skipped, never overwritten");
                Assert.AreEqual(sentinel, File.ReadAllText(sampleDisk),
                    "the planted file's bytes survive byte-identical");
            }
            finally
            {
                OpWeaveProjectSetup.CreateFileProbe = null;
            }
        }

        [Test]
        public void ProjectSetup_SampleCreate_PlantedDirectoryReportsFailure()
        {
            var sample = "Assets/OpWeave/Workflows/sample.opweave.json";
            var sampleDisk = Disk(sample);
            OpWeaveProjectSetup.CreateFileProbe = absolute =>
            {
                if (string.Equals(absolute, sampleDisk, StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(absolute);
                }
            };
            try
            {
                var result = OpWeaveProjectSetup.Run(ScratchProjectRoot);
                var item = result.Items.Single(i => i.Path == sample);
                Assert.That(item.State, Is.EqualTo(OpWeaveSetupItemState.Failed));
                Assert.That(Directory.Exists(sampleDisk), Is.True);
            }
            finally
            {
                OpWeaveProjectSetup.CreateFileProbe = null;
            }
        }

        // ── T-W5.3-03: an interrupted sample create leaves no partial
        //    file — the durable temp + atomic move means the target exists
        //    complete or not at all ──

        [Test]
        public void ProjectSetup_SampleCreate_InterruptedLeavesNoPartialFile()
        {
            // The injected flush failure stands in for an interruption
            // inside the same publish the sample create runs: the durable
            // temp write dies before the move, so the target stays absent,
            // the temp file is cleaned up, and the item reports Failed.
            var sample = "Assets/OpWeave/Workflows/sample.opweave.json";
            var sampleDisk = Disk(sample);
            Directory.CreateDirectory(Path.GetDirectoryName(sampleDisk)!);
            JsonSaveCoordinator.FlushToDiskForTesting =
                _ => throw new IOException("simulated interruption");
            try
            {
                var result = OpWeaveProjectSetup.Run(ScratchProjectRoot);
                var byPath = result.Items.ToDictionary(i => i.Path, i => i);
                Assert.That(byPath[sample].State,
                    Is.EqualTo(OpWeaveSetupItemState.Failed),
                    "the interrupted publish reports the item failed");
                Assert.IsFalse(File.Exists(sampleDisk),
                    "no partial sample file exists after the interrupted create");
                Assert.IsEmpty(
                    Directory.GetFiles(
                        Path.GetDirectoryName(sampleDisk)!, "*.tmp"),
                    "the durable temp file is cleaned up");
            }
            finally
            {
                JsonSaveCoordinator.FlushToDiskForTesting = null;
            }

            // The next run completes the create with the full sample bytes.
            var retry = OpWeaveProjectSetup.Run(ScratchProjectRoot);
            var retryByPath = retry.Items.ToDictionary(i => i.Path, i => i);
            Assert.That(retryByPath[sample].State,
                Is.EqualTo(OpWeaveSetupItemState.Created));
            Assert.AreEqual(OpWeaveProjectSetup.SampleWorkflowJson,
                File.ReadAllText(sampleDisk),
                "the retried create lands the complete sample");
        }

        // ── A failed item is reported and the run keeps going ──

        [Test]
        public void Run_ItemBlockedByFile_ReportsFailedAndContinues()
        {
            // A plain file where the Workflows folder must go makes that folder
            // (and the file under it) impossible to create — the rest must
            // still be made and the failures must be on the items.
            Directory.CreateDirectory(Disk("Assets/OpWeave"));
            File.WriteAllText(Disk("Assets/OpWeave/Workflows"), "occupied\n");

            var result = OpWeaveProjectSetup.Run(ScratchProjectRoot);
            var byPath = result.Items.ToDictionary(i => i.Path, i => i);
            Assert.That(byPath["Assets/OpWeave/Workflows"].State,
                Is.EqualTo(OpWeaveSetupItemState.Failed));
            Assert.That(byPath["Assets/OpWeave/Workflows/sample.opweave.json"].State,
                Is.EqualTo(OpWeaveSetupItemState.Failed));
            Assert.That(byPath["ProjectSettings/OpWeaveSettings.json"].State,
                Is.EqualTo(OpWeaveSetupItemState.Created),
                "unrelated items must still be created");
            Assert.IsFalse(OpWeaveProjectSetup.IsSetupComplete(ScratchProjectRoot),
                "a partial setup must not report complete");
        }

        // ── The generated OpWeave.Custom.asmdef is the SPEC §M.2 JSON ──

        [Test]
        public void GeneratedCustomAsmdef_HasSpecM2Content()
        {
            OpWeaveProjectSetup.Run(ScratchProjectRoot);
            var json = JObject.Parse(File.ReadAllText(Disk(
                "Assets/OpWeave/Editor/Operations/OpWeave.Custom.asmdef")));
            Assert.AreEqual("OpWeave.Custom", json["name"]!.ToString());
            Assert.AreEqual("OpWeave.Custom", json["rootNamespace"]!.ToString());
            CollectionAssert.AreEquivalent(
                new[] { "OpWeave.Core", "OpWeave.UnityAdapter" },
                json["references"]!.ToObject<string[]>());
            CollectionAssert.AreEquivalent(
                new[] { "Editor" },
                json["includePlatforms"]!.ToObject<string[]>());
            Assert.AreEqual(false, json["autoReferenced"]!.ToObject<bool>());
        }

        // ── The generated tests asmdef carries the test-side references ──

        [Test]
        public void GeneratedTestsAsmdef_ReferencesTestAssemblies()
        {
            OpWeaveProjectSetup.Run(ScratchProjectRoot);
            var json = JObject.Parse(File.ReadAllText(Disk(
                "Assets/OpWeave/Editor/Tests/OpWeave.Custom.Tests.asmdef")));
            Assert.AreEqual("OpWeave.Custom.Tests", json["name"]!.ToString());
            var references = json["references"]!.ToObject<string[]>()!;
            foreach (var required in new[]
            {
                "OpWeave.Core", "OpWeave.Custom", "OpWeave.Testing",
                "OpWeave.UnityAdapter", "UnityEngine.TestRunner",
                "UnityEditor.TestRunner",
            })
            {
                Assert.That(references, Does.Contain(required),
                    "missing reference " + required);
            }
            Assert.AreEqual(true, json["overrideReferences"]!.ToObject<bool>());
            CollectionAssert.AreEquivalent(
                new[] { "nunit.framework.dll" },
                json["precompiledReferences"]!.ToObject<string[]>());
            CollectionAssert.AreEquivalent(
                new[] { "UNITY_INCLUDE_TESTS" },
                json["defineConstraints"]!.ToObject<string[]>());
            CollectionAssert.AreEquivalent(
                new[] { "Editor" },
                json["includePlatforms"]!.ToObject<string[]>());
            Assert.AreEqual(false, json["autoReferenced"]!.ToObject<bool>());
        }

        // ── The generated settings file loads cleanly as v2 defaults ──

        [Test]
        public void GeneratedSettingsFile_LoadsAsCleanV2()
        {
            OpWeaveProjectSetup.Run(ScratchProjectRoot);
            var settingsPath = Disk("ProjectSettings/OpWeaveSettings.json");
            var text = File.ReadAllText(settingsPath);
            Assert.That(text, Does.Contain("\"version\": 2"),
                "the generated file must be the v2 form");

            var loaded = OpWeaveSettingsLoader.Load(settingsPath);
            Assert.IsTrue(loaded.Ok,
                "the generated file must load: " + string.Join(
                    "; ", loaded.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(loaded.Diagnostics, Is.Empty,
                "the generated file must load with no diagnostics");
            var settings = loaded.Value!;
            Assert.That(settings.DiscoveryMode, Is.EqualTo(DiscoveryMode.Auto));
            Assert.That(settings.IntegrityLevel, Is.EqualTo(IntegrityLevel.Weak));
            Assert.That(settings.CustomOperationFolders,
                Is.EqualTo(new[] { "Assets/OpWeave/Editor/Operations/" }),
                "customOperationFolders keeps its documented default; it sets " +
                "the create-menu default only, never the setup layout");
            Assert.IsFalse(settings.MigratedFromV1);
        }

        // ── The generated sample workflow parses and statically validates ──

        // [UnityTest]: the live RegistryBuildHost.Rebuild below must not
        // read a stale stage 1 left by a predecessor fixture's queued
        // compile, and the drain that drains it can only yield from a
        // test body's own top level (the T-W2.4-13 guard; a plain [Test]
        // cannot yield DrainedScriptReload).
        [UnityTest]
        public IEnumerator GeneratedSampleWorkflow_ParsesAndValidates()
        {
            // Drain any compile/reload a predecessor queued behind the
            // runner's lock BEFORE anything below captures or reads
            // state. Issue #621 made the drain the boundary the live
            // rebuild reads stage 1 across; issue #629 made it the
            // resume point UTF returns to with every non-serializable
            // local reset — `read` captured before the yield comes back
            // with Value null. Run, Read and the read.Ok assert all
            // execute after the crossing so nothing the resumed body
            // dereferences is a pre-yield capture. A null drain is that
            // resume, not a fresh start: rebuild the idempotent drain
            // and relay on instead of touching the dead reference.
            IEnumerator? drain = null;
            for (;;)
            {
                drain ??= DrainEditorState(
                    nameof(GeneratedSampleWorkflow_ParsesAndValidates));
                if (!drain.MoveNext())
                {
                    break;
                }
                yield return drain.Current;
            }

            // T-W2.4-15: a compile a predecessor armed can surface
            // queued-but-unstarted in the gap between the drain's exit
            // and the rebuild — the drain's terminal quiet check is not
            // the last word. If the armed compile surfaces into stage 1's
            // view the build reports Stale; if it is still invisible the
            // build reads mid-transition editor state whose products can
            // reach a null deref inside Rebuild/Validate. Re-drain and
            // rebuild, bounded, so the live build only ever comes from a
            // proven-quiet stage 1 — and everything the resumed body
            // later touches is captured AFTER this loop's last yield.
            RegistryBuildResult? build = null;
            ValidationReport? report = null;
            for (var leg = 0; ; leg++)
            {
                build = RegistryBuildHost.Rebuild(
                    RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                    RegistryOperationStage.Production(),
                    new RegistryPublication());
                if (!build.Stale && !ReloadPending())
                {
                    // Setup, parse and validate must all run inside the
                    // proven-quiet window: the drain→Validate gap is the
                    // T-W2.4-15 surface too, so re-check the pending probes
                    // before the compiler's provenance capture runs its live
                    // assembly queries. A compile that surfaces mid-window
                    // re-arms the leg instead of riding into Validate; the
                    // pure-filesystem setup is inert on the retry
                    // (Run_GeneratedLayoutCompiles_AndSecondRunIsInert).
                    OpWeaveProjectSetup.Run(ScratchProjectRoot);
                    var text = File.ReadAllText(Disk(
                        "Assets/OpWeave/Workflows/sample.opweave.json"));
                    var read = WorkflowSerializer.Read(text);
                    Assert.IsTrue(read.Ok, "the sample workflow must parse");
                    Assert.IsNotNull(read.Value,
                        "the parsed sample must carry a document");
                    Assert.IsNotNull(build.Snapshot,
                        "the registry build must publish a snapshot");
                    if (!ReloadPending())
                    {
                        var compiler = new WorkflowCompiler(
                            build.Snapshot!,
                            (nodeId, parameters) =>
                                new StubStaticContext(nodeId, parameters),
                            new UnityTypeProbe(),
                            UnityPlanProvenance.Capture(
                                IntegrityLevel.Weak, build.Providers));
                        report = compiler.Validate(new ValidationRequest(
                            read.Value!, null,
                            ValidationMode.Static, ActorKind.Agent));
                        break;
                    }
                }
                if (leg >= MaxRebuildLegs)
                {
                    Assert.Fail(
                        "ProjectSetupTests." +
                        nameof(GeneratedSampleWorkflow_ParsesAndValidates) +
                        ": the editor stayed armed through " +
                        MaxRebuildLegs + " re-drain legs");
                }
                IEnumerator? redrain = null;
                for (;;)
                {
                    redrain ??= DrainEditorState(
                        nameof(GeneratedSampleWorkflow_ParsesAndValidates) +
                        ".rebuild");
                    if (!redrain.MoveNext())
                    {
                        break;
                    }
                    yield return redrain.Current;
                }
            }
            Assert.IsNotNull(build,
                "the bounded rebuild must produce a build result");
            Assert.IsFalse(build!.Stale,
                "the registry build must not be stale");
            Assert.IsNotNull(build.Snapshot,
                "the registry build must publish a snapshot");
            Assert.IsNotNull(report,
                "the bounded legs must reach a validation report");
            Assert.That(report!.Ok, Is.True,
                "validation errors: " + string.Join(
                    "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.That(report.Warnings, Is.Empty,
                "the sample must validate warning-free, got " +
                    string.Join("; ", report.Warnings.Select(d => d.Code)));
        }

        // ── The Tools > OpWeave > Setup Project entry exists (SPEC §M.3 item 1) ──

        [Test]
        public void SetupProjectMenu_IsRegistered()
        {
            var methods = typeof(OpWeaveProjectSetup).GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var entry = methods.SelectMany(
                m => m.GetCustomAttributes<MenuItem>().Select(
                    a => (method: m, attribute: a)))
                .FirstOrDefault(pair =>
                    pair.attribute.menuItem == "Tools/OpWeave/Setup Project");
            Assert.IsNotNull(entry.method,
                "the Tools/OpWeave/Setup Project menu entry is missing");
            Assert.IsTrue(entry.method!.IsStatic,
                "the menu method must be static");
        }

        // ── The Console listing names created and skipped items ──

        [Test]
        public void LogResult_ListsCreatedAndSkippedToConsole()
        {
            Directory.CreateDirectory(Disk("Assets/OpWeave/Editor/Operations"));
            var result = OpWeaveProjectSetup.Run(ScratchProjectRoot);
            Assert.That(result.Created, Is.Not.Empty);
            Assert.That(result.Skipped, Is.Not.Empty);

            var logged = new List<string>();
            Application.LogCallback handler = (condition, _, _) => logged.Add(condition);
            Application.logMessageReceived += handler;
            try
            {
                OpWeaveProjectSetup.LogResult(result);
            }
            finally
            {
                Application.logMessageReceived -= handler;
            }

            Assert.That(logged.Count, Is.EqualTo(1),
                "the summary must be a single Console entry");
            var message = logged[0];
            StringAssert.Contains("Created:", message);
            StringAssert.Contains("Skipped:", message);
            StringAssert.Contains("Assets/OpWeave", message);
            StringAssert.Contains("Assets/OpWeave/Editor/Operations", message);
            StringAssert.Contains("ProjectSettings/OpWeaveSettings.json", message);
        }

        // ── The generated asmdef shape compiles across a domain reload ──

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // A per-Editor-session token the pre-reload phase stores in
        // SessionState and writes to the session record; the post-reload
        // checks read both to prove the phase ran in this session — a
        // stale resume that skipped it (no file, no nonce), a record
        // deleted by cleanup, and residue of an earlier session (file
        // yes, nonce no) each fail closed, never silently. The record
        // sits beside the scratch root (never inside it) so the
        // [UnitySetUp]/[UnityTearDown] scratch clean cannot erase it.
        private static string ProbeSessionFile =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveSetupProbe", "session.txt");

        private const string ProbeSessionKey = "OpWeave.SetupProbe.ProbeToken";

        // Bound on phase-1 re-drive legs after a stale resume is
        // detected (the T-W2.4-06 self-heal); the this-session probe
        // check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static string ArmProbeNonce()
        {
            var nonce = Guid.NewGuid().ToString("N");
            SessionState.SetString(ProbeSessionKey, nonce);
            return nonce;
        }

        private static bool ProbeWrittenThisSession()
        {
            if (!File.Exists(ProbeSessionFile))
            {
                return false;
            }
            var expected = SessionState.GetString(ProbeSessionKey, "");
            if (expected.Length == 0)
            {
                return false;
            }
            return File.ReadAllText(ProbeSessionFile).Trim() == expected;
        }

        // The fail-closed post-reload check: distinguishes a phase that
        // never ran in this session (no record, no armed nonce) from a
        // record deleted after it ran — and rejects residue outright.
        private static void AssertProbeSession()
        {
            var expected = SessionState.GetString(ProbeSessionKey, "");
            if (!File.Exists(ProbeSessionFile))
            {
                Assert.Fail(
                    expected.Length == 0
                        ? "the pre-reload phase never executed in this Editor " +
                            "session (" + ProbeSessionFile + " was never written)"
                        : "the pre-reload session record " + ProbeSessionFile +
                            " was deleted after the phase wrote it");
            }
            Assert.That(
                File.ReadAllText(ProbeSessionFile).Trim(),
                Is.EqualTo(expected),
                "the pre-reload session record is stale — a previous Editor " +
                "session or an abandoned leg wrote it; refusing to trust it");
        }

        private static string ProbeDisk(string assetsRelative) =>
            Path.Combine(
                ProjectRoot,
                assetsRelative.Replace('/', Path.DirectorySeparatorChar));

        // Setup and teardown must never yield an ExpectDomainReload
        // instruction (RecompileScripts): UTF's PrepareForDomainReload
        // records the CURRENT TestEnumerator's program counter — still a
        // different test's method enumerator while setup or teardown is
        // running — and the resume then applies that stale PC to the next
        // test method's fresh enumerator, jumping it past phase-1
        // entirely (T-W2.4-10). The probe-folder deletion above queues
        // its compile under the runner's LockReloadAssemblies, so the
        // reload it needs drains at the next test body's own
        // `yield return new RecompileScripts()`, whose captured PC is the
        // correct one.
        //
        // The de-yield also keeps the intentional-compile-failure
        // semantic the old `expectScriptCompilationSuccess:false` wait
        // carried: the cleanup compile is queued, never awaited here —
        // the UnityTest's own post-reload assertions
        // (`scriptCompilationFailed` / `isCompiling`) are what prove the
        // compile outcome, and a probe whose generated asmdef legitimately
        // fails to compile fails THAT test loudly rather than through a
        // stale flag a teardown wait consumed.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var dirty = CleanupProbe();
            CleanScratch();
            // The scratch root the tests run setup against must exist even
            // where a test only reads the tree: create it after the cleanup
            // that deletes the probe root.
            Directory.CreateDirectory(ScratchProjectRoot);
            Crumb("setup cleanup exit; assetsDirty=" + dirty);
            yield break;
        }

        // Fail-safe: whatever the test left behind — a failed assertion, an
        // exception, even probe scripts that do not compile — the probe
        // folder is removed and the cleaned tree's compile is queued, so a
        // failing probe never leaves a compile error for the next test.
        // No reload emitter is yielded here (see the setup note above):
        // the queued compile drains at the next sanctioned boundary.
        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var dirty = CleanupProbe();
            CleanScratch();
            Crumb("teardown cleanup exit; assetsDirty=" + dirty);
            yield break;
        }

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[W2903] " + message);

        private static bool CleanupProbe()
        {
            var dirty = false;
            if (AssetDatabase.IsValidFolder(ProbeAssetsDir))
            {
                AssetDatabase.DeleteAsset(ProbeAssetsDir);
                dirty = true;
            }
            var disk = ProbeDisk(ProbeAssetsDir);
            if (Directory.Exists(disk))
            {
                try
                {
                    Directory.Delete(disk, recursive: true);
                    dirty = true;
                }
                catch (Exception)
                {
                    // a held probe file is left for the next run's cleanup
                }
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            return dirty;
        }

        // The generated asmdef JSON, name-swapped for a unique probe assembly:
        // the committed OpWeave.Custom fixture already claims that name in this
        // project, so the real generated file cannot also live here — the swap
        // keeps every other byte (references, platforms, constraints) exactly
        // what setup wrote.
        private static string ProbeAsmdef(string generated, string probeName)
        {
            var patched = generated
                .Replace("\"OpWeave.Custom.Tests\"", "\"" + ProbeTestsAssembly + "\"")
                .Replace("\"OpWeave.Custom\"", "\"" + ProbeCustomAssembly + "\"");
            return patched;
        }

        private const string ProbeCustomMarker =
            "#nullable enable\n" +
            "namespace OpWeaveSetupProbe\n{\n" +
            // Public: the tests assembly references it with typeof — the same
            // cross-assembly reach a user test has into OpWeave.Custom.
            "    public static class SetupProbeMarker\n" +
            "    {\n" +
            "        internal static readonly string Core =\n" +
            "            typeof(OpWeave.Core.CoreApi).Name;\n" +
            "        internal static readonly string Adapter =\n" +
            "            typeof(OpWeave.UnityAdapter.OperationBase).Name;\n" +
            "    }\n" +
            "}\n";

        private const string ProbeTestsMarker =
            "#nullable enable\n" +
            "namespace OpWeaveSetupProbe\n{\n" +
            "    internal static class SetupProbeTestsMarker\n" +
            "    {\n" +
            "        internal static readonly string Testing =\n" +
            "            typeof(OpWeave.Testing.OperationContract).Name;\n" +
            "        internal static readonly string Custom =\n" +
            "            typeof(SetupProbeMarker).Name;\n" +
            "        internal static readonly string NUnit =\n" +
            "            typeof(NUnit.Framework.TestAttribute).Name;\n" +
            "        internal static readonly string EngineRunner =\n" +
            "            typeof(UnityEngine.TestTools.UnityTestAttribute).Name;\n" +
            "        internal static readonly string EditorRunner =\n" +
            "            typeof(UnityEditor.TestTools.TestRunner.Api.TestRunnerApi).Name;\n" +
            "    }\n" +
            "}\n";

        // The plain result a phase helper hands back to the iterator: no
        // captured state, only a pass flag plus the full failure text the
        // iterator asserts on. [Serializable] so UTF's iterator
        // preservation across the domain reload can carry it too.
        [Serializable]
        private sealed class ProbePhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static ProbePhaseResult Pass() => new ProbePhaseResult { Ok = true };
            public static ProbePhaseResult Fail(string message) =>
                new ProbePhaseResult { Ok = false, Failure = message };
        }

        // Phase 1 (pre-reload): the first setup run must create the whole
        // layout, then the generated asmdef JSON (name-swapped) and one
        // marker script per probe assembly land under Assets/, the session
        // nonce is persisted and the reload armed. Nothing is assumed about
        // the domain — the scratch tree and probe assets are rebuilt by a
        // re-drive leg's cleanup.
        private static ProbePhaseResult RunPhaseBeforeReload()
        {
            try
            {
                var first = OpWeaveProjectSetup.Run(ScratchProjectRoot);
                AssertEveryItem(first, OpWeaveSetupItemState.Created);

                // Plant the generated asmdef content (name-swapped) under
                // Assets/ with one marker script each, so the recompile
                // actually builds the generated reference set.
                Directory.CreateDirectory(ProbeDisk(ProbeCustomDir));
                Directory.CreateDirectory(ProbeDisk(ProbeTestsDir));
                File.WriteAllText(
                    Path.Combine(ProbeDisk(ProbeCustomDir), "OpWeave.Custom.asmdef"),
                    ProbeAsmdef(
                        File.ReadAllText(Disk(
                            "Assets/OpWeave/Editor/Operations/OpWeave.Custom.asmdef")),
                        ProbeCustomAssembly));
                File.WriteAllText(
                    Path.Combine(ProbeDisk(ProbeCustomDir), "SetupProbeMarker.cs"),
                    ProbeCustomMarker);
                File.WriteAllText(
                    Path.Combine(ProbeDisk(ProbeTestsDir), "OpWeave.Custom.Tests.asmdef"),
                    ProbeAsmdef(
                        File.ReadAllText(Disk(
                            "Assets/OpWeave/Editor/Tests/OpWeave.Custom.Tests.asmdef")),
                        ProbeTestsAssembly));
                File.WriteAllText(
                    Path.Combine(ProbeDisk(ProbeTestsDir), "SetupProbeTestsMarker.cs"),
                    ProbeTestsMarker);
                Directory.CreateDirectory(Path.GetDirectoryName(ProbeSessionFile)!);
                File.WriteAllText(ProbeSessionFile, ArmProbeNonce() + "\n");
                AssetDatabase.Refresh();
                ArmDomainSentinel();
                Crumb("phase-1 complete: layout created, probe asmdefs planted");
                return ProbePhaseResult.Pass();
            }
            catch (Exception e)
            {
                return ProbePhaseResult.Fail(
                    "phase-1 threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Run_GeneratedLayoutCompiles_AndSecondRunIsInert()
        {
            var phase1 = RunPhaseBeforeReload();
            Crumb("phase-1 ok=" + phase1.Ok + " failure=" + phase1.Failure);
            Assert.That(phase1.Ok, Is.True,
                "pre-reload phase failed: " + phase1.Failure);

            // T-W2.4-06 stale-resume self-heal: a predecessor's captured
            // UTF resume PC applies to this fresh enumerator and lands it
            // at a post-yield state inside this loop — with every local
            // (leg included) reset to default. Each leg drains, then MUST
            // pass the this-session probe check: a session record the
            // phase never wrote re-drives a real phase-1 + compile +
            // reload instead of skipping silently into phase-2. After
            // the bound the loop exits and AssertProbeSession's
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
                // A probe tree an abandoned leg left behind carries
                // identical bytes and the scratch tree is already a
                // second-run state, so deleting both guarantees the
                // re-write queues a real compile and the re-run a real
                // first-run.
                CleanupProbe();
                CleanScratch();
                Directory.CreateDirectory(ScratchProjectRoot);
                var redrive = RunPhaseBeforeReload();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            AssertProbeSession();
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the generated asmdef content must compile");
            Assert.IsFalse(EditorApplication.isCompiling);

            var assemblies = CompilationPipeline
                .GetAssemblies(AssembliesType.Editor)
                .Select(a => a.name)
                .ToList();
            Assert.That(assemblies, Does.Contain(ProbeCustomAssembly),
                "the generated OpWeave.Custom shape must compile");
            Assert.That(assemblies, Does.Contain(ProbeTestsAssembly),
                "the generated OpWeave.Custom.Tests shape must compile");

            // Second run in the new domain: every item skipped, tree unchanged.
            var before = TreeSnapshot();
            var second = OpWeaveProjectSetup.Run(ScratchProjectRoot);
            AssertEveryItem(second, OpWeaveSetupItemState.Skipped);
            Assert.That(TreeSnapshot(), Is.EqualTo(before),
                "the second run must change nothing");
        }

        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;

            internal StubStaticContext(
                string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }

            public override string NodeId { get; }

            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException("the fixture context resolves nothing");
        }
    }
}
