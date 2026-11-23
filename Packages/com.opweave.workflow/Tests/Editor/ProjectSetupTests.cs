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

        [Test]
        public void GeneratedSampleWorkflow_ParsesAndValidates()
        {
            OpWeaveProjectSetup.Run(ScratchProjectRoot);
            var text = File.ReadAllText(Disk(
                "Assets/OpWeave/Workflows/sample.opweave.json"));
            var read = WorkflowSerializer.Read(text);
            Assert.IsTrue(read.Ok, "the sample workflow must parse");

            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale, "the registry build must not be stale");
            var compiler = new WorkflowCompiler(
                build.Snapshot!,
                (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                new UnityTypeProbe(),
                UnityPlanProvenance.Capture(IntegrityLevel.Weak, build.Providers));
            var report = compiler.Validate(new ValidationRequest(
                read.Value!, null, ValidationMode.Static, ActorKind.Agent));
            Assert.That(report.Ok, Is.True,
                () => "validation errors: " + string.Join(
                    "; ", report.Errors.Select(d => d.Code + " " + d.Message)));
            Assert.That(report.Warnings, Is.Empty,
                () => "the sample must validate warning-free, got " +
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

        private static string ProbeDisk(string assetsRelative) =>
            Path.Combine(
                ProjectRoot,
                assetsRelative.Replace('/', Path.DirectorySeparatorChar));

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var dirty = CleanupProbe();
            CleanScratch();
            // The scratch root the tests run setup against must exist even
            // where a test only reads the tree: create it after the cleanup
            // that deletes the probe root.
            Directory.CreateDirectory(ScratchProjectRoot);
            if (dirty)
            {
                yield return new RecompileScripts(
                    expectScriptCompilation: true, expectScriptCompilationSuccess: false);
                yield return WaitForCompileIdle();
            }
        }

        // Fail-safe: whatever the test left behind — a failed assertion, an
        // exception, even probe scripts that do not compile — the probe folder
        // is removed and a compile of the cleaned tree is forced and waited
        // out, so a failing probe never leaves a compile error for the next
        // test. The wait must not assert success: RecompileScripts with the
        // default success expectation throws on the probe's own stale
        // scriptCompilationFailed flag before the cleanup compile clears it.
        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var dirty = CleanupProbe();
            CleanScratch();
            if (dirty)
            {
                yield return new RecompileScripts(
                    expectScriptCompilation: true, expectScriptCompilationSuccess: false);
                yield return WaitForCompileIdle();
            }
        }

        // Spins out the recompile's tail without asserting the outcome —
        // the cleanup compile, not its verdict, is the teardown contract.
        // Only plain nulls are yielded, so this stays legal inside the
        // UnitySetUp/UnityTearDown enumerators: UTF rejects a nested
        // enumerator that yields RecompileScripts, so the request itself
        // must stay inline in the setup/teardown bodies above.
        private static IEnumerator WaitForCompileIdle()
        {
            var guard = 0;
            while (EditorApplication.isCompiling && guard++ < 600)
            {
                yield return null;
            }
        }

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

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Run_GeneratedLayoutCompiles_AndSecondRunIsInert()
        {
            var first = OpWeaveProjectSetup.Run(ScratchProjectRoot);
            AssertEveryItem(first, OpWeaveSetupItemState.Created);

            // Plant the generated asmdef content (name-swapped) under Assets/
            // with one marker script each, so the recompile actually builds the
            // generated reference set.
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
            AssetDatabase.Refresh();
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");
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
