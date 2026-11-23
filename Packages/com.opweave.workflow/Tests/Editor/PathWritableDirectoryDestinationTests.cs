#nullable enable

// T-W2.7-05 [Edit] tests: local:T-W2.7-05:create-folder-existing — the
// path_writable remediation for an existing-directory destination,
// exercised through the Runner side of the seam: the
// OperationTestHarness judges the step's Requirements with the same
// live evaluator the Runner host wires, before Preflight and Execute
// (SPEC §E4 step 2).
//
// Coverage:
//  - create_folder on the same nested path twice: the second run
//    succeeds with an equal output, records no change and creates no
//    new asset; the T-W2.10-02 AssertIdempotency contract assertion
//    passes (SPEC §G7 item 3, §I.3 "既存なら成功", §F5 Idempotent).
//  - The Runner-level half of that acceptance (the review's F1): the
//    same nested create_folder workflow runs TWICE through the real
//    OpWeaveRunnerHost, whose UnityStepContext.BoundOperationId the
//    RequirementEvaluatorAdapter hands the live §G1 evaluator — the
//    second Run is again the idempotent success with equal output, no
//    recorded change and no new asset. The negative half runs
//    unity.asset.create_material on an existing directory: the
//    file-destination reading keeps its OW-UNITY-010 refusal before
//    step_started.
//  - The refusal cases keep their codes: '..' and outside
//    allowedPaths OW-PERM-002, an immutable package OW-UNITY-010, a
//    regular file at the folder path OW-UNITY-005, an unwritable
//    parent OW-UNITY-010.
//  - The ticket's audit of the other path_writable users on an
//    existing-directory destination. §I.4's folder* names the folder
//    the script lands in and the Operation's own contract requires it
//    to exist — the SPEC row requires the acceptance, so
//    create_from_template accepts. Every file-destination user keeps
//    its main refusal exactly where it was: an existing directory is
//    not a writable file destination, so the declared path_writable
//    requirement refuses with OW-UNITY-010 before Preflight or
//    Execute run — the ticket's scope bound ("Apply this only where
//    the requirement's destination is a folder").
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Operations;
using OpWeave.Testing;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The T-W2.7-05 Runner-side fixtures: an existing directory as a
    /// <c>path_writable</c> destination and the per-Operation audit the
    /// ticket requires.
    /// </summary>
    internal sealed class PathWritableDirectoryDestinationTests
    {
        private const string CreateFolderId = "unity.asset.create_folder";
        private const string RunnerWorkflowId = "example.path-writable-destination";

        // The RunnerHostFixture state of the Runner-level half: one Run
        // Store root per test, the fixtures it serves and the probe
        // folders the asset paths point at.
        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<string> _ownedAssetPaths = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeavePathWritableRunnerTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
            foreach (var assetPath in _ownedAssetPaths)
            {
                if (AssetDatabase.IsValidFolder(assetPath))
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
            }
        }

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string Physical(string projectPath) =>
            Path.Combine(
                ProjectRoot(), projectPath.Replace('/', Path.DirectorySeparatorChar));

        private static void AssertRefused(HarnessResult result, string code)
        {
            Assert.That(result.StepState, Is.EqualTo(StepState.Failed),
                () => result.ToString());
            Assert.That(result.Diagnostics, Is.Not.Empty);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo(code));
        }

        private static string MakeFolder(string parent, string name)
        {
            var guid = AssetDatabase.CreateFolder(parent, name);
            Assert.That(guid, Is.Not.Null.And.Not.Empty,
                $"the fixture folder '{parent}/{name}' must be created");
            return parent + "/" + name;
        }

        // A RunnerHostFixture over the test's Run Store root; liveRequirements
        // binds the production §G1 evaluator — the real file-system and
        // package probes — instead of the fixture's always-satisfied stubs,
        // so the (Operation, port) destination kind BoundOperationId selects
        // decides the path_writable verdict.
        private RunnerHostFixture NewFixture(bool liveRequirements)
        {
            var fixture = new RunnerHostFixture(_root)
            {
                LiveRequirements = liveRequirements,
            };
            _fixtures.Add(fixture);
            return fixture;
        }

        // MakeFolder that also registers the path for TearDown deletion —
        // the probe folders the Runner-level tests point their plans at.
        private string OwnFolder(string parent, string name)
        {
            var path = MakeFolder(parent, name);
            _ownedAssetPaths.Add(path);
            return path;
        }

        // Drives the run forward one editor-update slice at a time, as the
        // production EditorApplication.update subscription does (the event
        // itself is never invoked from tests).
        private static void Drive(OpWeaveRunnerHost host, string runId, int maxTicks = 50)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    return;
                }
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not reach a terminal state within " + maxTicks + " ticks.");
        }

        // Admits one Run of the plan and drives it to a terminal state;
        // returns its Run id for the journal/store assertions.
        private static string AcceptAndDrive(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument document)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, document, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture.Host, runId);
            return runId;
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        // The Run's finalized §F7 change list — Runs/<runId>/changes.json.
        private static JArray ReadChanges(string root, string runId)
        {
            var path = Path.Combine(root, "Runs", runId, "changes.json");
            Assert.IsTrue(
                File.Exists(path), "the Run Store holds no changes.json for " + runId);
            var parsed = JsonValue.Parse(File.ReadAllText(path));
            Assert.IsTrue(parsed.Ok, "changes.json must parse");
            var token = parsed.Value!.ToToken();
            Assert.IsInstanceOf<JArray>(token, "changes.json is a change array (SPEC §F7)");
            return (JArray)token;
        }

        // ── local:T-W2.7-05:create-folder-existing — the second run ─────

        // SPEC §G7 item 3 / §I.3 "既存なら成功" / §F5 Idempotent: a nested
        // path created once and requested again — the second run is the
        // success the requirement used to refuse with OW-UNITY-010.
        [Test]
        public void CreateFolder_RerunOnExistingFolderSucceedsWithEqualOutputAndNoNewAsset()
        {
            using var harness = OperationTestHarness.Create();
            var path = harness.TempFolder + "/Nested/Deep";

            var first = harness.Run(CreateFolderId, new { path });
            Assert.That(first.Ok, Is.True, () => first.ToString());
            Assert.IsTrue(AssetDatabase.IsValidFolder(path),
                "the first run creates the nested folder");
            var afterFirst = Directory.GetFileSystemEntries(
                Physical(harness.TempFolder), "*", SearchOption.AllDirectories).Length;

            var second = harness.Run(CreateFolderId, new { path });

            Assert.That(second.Ok, Is.True, () => second.ToString());
            Assert.AreEqual(
                first.Outputs["path"].CanonicalText,
                second.Outputs["path"].CanonicalText,
                "the second run reports the same path output");
            Assert.That(second.Changes, Is.Empty,
                "the idempotent second run records no change");
            Assert.AreEqual(
                afterFirst,
                Directory.GetFileSystemEntries(
                    Physical(harness.TempFolder), "*", SearchOption.AllDirectories).Length,
                "the second run creates no new asset");
        }

        // The T-W2.10-02 AssertIdempotency contract assertion the ticket
        // names: the §F5 two-run check must hold for create_folder.
        [Test]
        public void CreateFolder_IdempotencyContractAssertionPasses()
        {
            using var harness = OperationTestHarness.Create();
            Assert.DoesNotThrow(() => OperationContract.AssertIdempotency(
                CreateFolderId, new { path = harness.TempFolder + "/Idem" }));
        }

        // ── The Runner-level regression (the review's F1) ──────────────

        // The acceptance the harness cannot reach: the real
        // OpWeaveRunnerHost builds the step's UnityStepContext, whose
        // BoundOperationId the RequirementEvaluatorAdapter hands the live
        // §G1 evaluator — create_folder's 'path' is then judged by the
        // folder-destination reading (SPEC §G1 item 4). The same nested
        // workflow runs TWICE through the host: the first Run creates the
        // folder, the second is the §I.3 "既存なら成功" idempotent success
        // — equal output, no recorded change, no new asset — not the
        // OW-UNITY-010 the anonymous file-destination reading the defect
        // report came from keeps producing.
        [Test]
        public void CreateFolder_RunnerRerunOnExistingFolderSucceedsWithEqualOutputAndNoRecordedChange()
        {
            var fixture = NewFixture(liveRequirements: true);
            var probe = OwnFolder(
                "Assets", "OpWeaveW27RunnerProbe" + Guid.NewGuid().ToString("N"));
            var path = probe + "/Nested/Deep";

            var createFolder = new CreateFolderOperation();
            fixture.Register(createFolder, 'a');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "mkdir", createFolder.Descriptor.Id, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["path"] = new Constant(
                            RunnerHostFixture.Json("\"" + path + "\"")),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["path"] = "unity.AssetPath",
                    },
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(RunnerWorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                RunnerWorkflowId,
                new[]
                {
                    new NodeData
                    {
                        Id = "mkdir",
                        Operation = createFolder.Descriptor.Id,
                        Version = 1,
                    },
                },
                Array.Empty<ControlEdge>());

            var firstRunId = AcceptAndDrive(fixture, plan, doc);
            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(firstRunId));
            Assert.IsTrue(AssetDatabase.IsValidFolder(path),
                "the first Run creates the nested folder");
            var afterFirst = Directory.GetFileSystemEntries(
                Physical(probe), "*", SearchOption.AllDirectories).Length;

            var secondRunId = AcceptAndDrive(fixture, plan, doc);
            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(secondRunId),
                "the second Run is the §I.3 idempotent success, not OW-UNITY-010");

            // Equal output: both Runs' step_succeeded record the same
            // normalized path output.
            var outputsFragment = "\"outputs\":{\"path\":\"" + path + "\"}";
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, firstRunId, "\"type\":\"step_succeeded\"", outputsFragment),
                Is.True, "the first Run did not journal the path output");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, secondRunId, "\"type\":\"step_succeeded\"", outputsFragment),
                Is.True, "the second Run reports the same path output");

            // No recorded change on the rerun — in the step_succeeded
            // record and in the Run's finalized changes.json.
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, secondRunId, "\"type\":\"step_succeeded\"", "\"changes\":[]"),
                Is.True, "the idempotent rerun recorded a change");
            Assert.That(ReadChanges(_root, secondRunId), Is.Empty,
                "the rerun's changes.json is the empty list");

            // No new asset: the probe folder's file-system content is
            // exactly what the first Run left.
            Assert.AreEqual(
                afterFirst,
                Directory.GetFileSystemEntries(
                    Physical(probe), "*", SearchOption.AllDirectories).Length,
                "the second Run created a new asset");
        }

        // The Runner-level half of the ticket's audit bound: through the
        // same BoundOperationId wiring a file-destination path_writable
        // port keeps its on-main refusal — unity.asset.create_material's
        // 'path' naming an existing directory is OW-UNITY-010 from the
        // §G1 requirement, before step_started (SPEC §E4 step 2), never
        // reaching the Operation's own OW-UNITY-005 occupancy check.
        [Test]
        public void Audit_FileDestinationUser_RunnerRefusesAnExistingDirectoryWithOwUnity010()
        {
            var fixture = NewFixture(liveRequirements: true);
            var probe = OwnFolder(
                "Assets", "OpWeaveW27RunnerNeg" + Guid.NewGuid().ToString("N"));
            var taken = MakeFolder(probe, "Taken.mat");

            var createMaterial = new CreateMaterialOperation();
            fixture.Register(createMaterial, 'a');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "material", createMaterial.Descriptor.Id, 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["path"] = new Constant(
                            RunnerHostFixture.Json("\"" + taken + "\"")),
                        ["shader"] = new Constant(
                            RunnerHostFixture.Json("\"Standard\"")),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["material"] = "unity.AssetRef<unity.Material>",
                    },
                    idempotency: Idempotency.Reconcilable),
            };
            var plan = RunnerHostFixture.Plan(RunnerWorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                RunnerWorkflowId,
                new[]
                {
                    new NodeData
                    {
                        Id = "material",
                        Operation = createMaterial.Descriptor.Id,
                        Version = 1,
                    },
                },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture.Host, runId);

            Assert.AreEqual(RunState.Failed, fixture.Host.Coordinator.StateOf(runId));
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_failed\"", "\"error\":\"OW-UNITY-010\""),
                Is.True, "the step_failed did not carry OW-UNITY-010");
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_started\"", "\"step\":0,"),
                "a violated path_writable must not reach StepStarted — the " +
                "refusal precedes Preflight and Execute (SPEC §E4 step 2)");
        }

        // ── The refusal cases keep their codes ─────────────────────────

        // A regular file stands where the folder is asked for: a
        // writable file satisfies path_writable (as on main) and the
        // Operation's own occupancy check refuses OW-UNITY-005.
        [Test]
        public void CreateFolder_FileAtFolderPathStillRefused()
        {
            using var harness = OperationTestHarness.Create();
            var taken = harness.TempFolder + "/taken";
            File.WriteAllText(Physical(taken), "probe");
            AssetDatabase.ImportAsset(taken);

            var result = harness.Run(CreateFolderId, new { path = taken });

            AssertRefused(result, ErrorCodes.OW_UNITY_005);
        }

        // '..' and a path outside the settings allowedPaths stay
        // OW-PERM-002 — the check that runs before the disk probe.
        [Test]
        public void CreateFolder_TraversalAndOutsideAllowedPathsStillRefused()
        {
            using var harness = OperationTestHarness.Create();

            AssertRefused(
                harness.Run(CreateFolderId,
                    new { path = "Assets/../ProjectSettings/evil" }),
                ErrorCodes.OW_PERM_002);
            AssertRefused(
                harness.Run(CreateFolderId, new { path = "Library/outside" }),
                ErrorCodes.OW_PERM_002);
        }

        // An immutable package destination stays OW-UNITY-010 through
        // the Runner: the harness's evaluator judges against the
        // settings snapshot — a temporary allowedPaths widening to
        // Packages/ reaches the package-mutability half (the project's
        // own settings file, absent by default, is restored exactly).
        [Test]
        public void CreateFolder_ImmutablePackageDestinationStillRefused()
        {
            var settingsDisk = Path.Combine(
                ProjectRoot(), "ProjectSettings", "OpWeaveSettings.json");
            var prior = File.Exists(settingsDisk)
                ? File.ReadAllText(settingsDisk)
                : null;
            try
            {
                File.WriteAllText(
                    settingsDisk,
                    "{\"version\":2,\"allowedPaths\":[\"Assets/\",\"Packages/\"]}");

                using var harness = OperationTestHarness.Create();
                var result = harness.Run(CreateFolderId,
                    new { path = "Packages/com.unity.test-framework/NewFolder" });

                AssertRefused(result, ErrorCodes.OW_UNITY_010);
            }
            finally
            {
                if (prior is null)
                {
                    File.Delete(settingsDisk);
                }
                else
                {
                    File.WriteAllText(settingsDisk, prior);
                }
            }
        }

        // An unwritable parent directory stays OW-UNITY-010: the probe
        // cannot create its file under the denied ancestor (Windows ACL).
        [Test]
        [Platform("Win")]
        public void CreateFolder_UnwritableParentStillRefused()
        {
            using var harness = OperationTestHarness.Create();
            var blocked = MakeFolder(harness.TempFolder, "Blocked");
            var blockedDisk = Physical(blocked);
            var identity = Environment.UserDomainName + "\\" + Environment.UserName;
            var denied = false;
            try
            {
                if (!TryIcacls($"\"{blockedDisk}\" /deny \"{identity}\":(W)", out var reason))
                {
                    Assert.Ignore(
                        $"icacls could not deny write on the probe directory: {reason}");
                }
                denied = true;

                var result = harness.Run(CreateFolderId, new { path = blocked + "/Child" });

                AssertRefused(result, ErrorCodes.OW_UNITY_010);
            }
            finally
            {
                if (denied)
                {
                    TryIcacls($"\"{blockedDisk}\" /remove:d \"{identity}\"", out _);
                }
            }
        }

        // ── The path_writable user audit (ticket acceptance) ───────────

        // SPEC §I.4: folder* names the folder the generated script lands
        // in, and the Operation's own contract requires it to exist — a
        // folder destination, so the existing writable folder is
        // accepted and the script is written into it. (On main the
        // requirement refused the very folder the Operation needs with
        // OW-UNITY-010, so the Operation could never succeed through a
        // Run.)
        [Test]
        public void Audit_CreateFromTemplate_AcceptsAnExistingFolder()
        {
            using var harness = OperationTestHarness.Create();
            var className = "AuditFolderProbe";

            var result = harness.Run("unity.script.create_from_template", new
            {
                template = "mono_behaviour",
                className,
                folder = harness.TempFolder,
            });

            Assert.That(result.Ok, Is.True, () => result.ToString());
            Assert.IsTrue(
                File.Exists(Physical(harness.TempFolder + "/" + className + ".cs")),
                "the script is written into the existing folder");
        }

        // The file-destination users keep their refusal exactly where it
        // was on main: an existing directory is not a writable file
        // destination, so the declared path_writable requirement refuses
        // with OW-UNITY-010 before the Operation's Preflight or Execute
        // runs — the ticket's scope bound keeps the folder-destination
        // reading to create_folder's 'path' and create_from_template's
        // 'folder'.
        [Test]
        public void Audit_FileDestinationUsers_StillRefuseAnExistingDirectory()
        {
            using var harness = OperationTestHarness.Create();
            var material = MakeFolder(harness.TempFolder, "Taken.mat");
            var scene = MakeFolder(harness.TempFolder, "Taken.unity");
            var asset = MakeFolder(harness.TempFolder, "Taken.asset");
            var saved = MakeFolder(harness.TempFolder, "Saved.unity");
            var imported = MakeFolder(harness.TempFolder, "Imported.txt");

            AssertRefused(
                harness.Run("unity.asset.create_material",
                    new { path = material, shader = "Standard" }),
                ErrorCodes.OW_UNITY_010);
            AssertRefused(
                harness.Run("unity.scene.create", new { path = scene }),
                ErrorCodes.OW_UNITY_010);
            AssertRefused(
                harness.Run("unity.asset.create_scriptable_object",
                    new { path = asset, type = typeof(ProbeFieldsAsset).FullName! }),
                ErrorCodes.OW_UNITY_010);
            AssertRefused(
                harness.Run("unity.scene.save", new { path = saved }),
                ErrorCodes.OW_UNITY_010);
            AssertRefused(
                harness.Run("unity.asset.import", new { path = imported }),
                ErrorCodes.OW_UNITY_010);
        }

        // Runs icacls synchronously and reports whether it exited
        // successfully; false — with the captured output or the launch
        // failure as <paramref name="output"/> — when icacls is
        // unavailable or fails.
        private static bool TryIcacls(string arguments, out string output)
        {
            try
            {
                var info = new ProcessStartInfo("icacls", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var process = Process.Start(info))
                {
                    if (process is null)
                    {
                        output = "icacls did not start";
                        return false;
                    }
                    output = process.StandardOutput.ReadToEnd() +
                        process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception e)
            {
                output = e.Message;
                return false;
            }
        }
    }
}
