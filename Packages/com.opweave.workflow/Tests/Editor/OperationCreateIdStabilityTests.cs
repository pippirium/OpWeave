#nullable enable

// T-W2.9-07 [Edit] tests: the SPEC §A4/§M.4 stability half of
// local:T-W2.9-07:create-operation-uuid plus the
// local:T-W2.9-07:create-operation-id-restart two-process fixture.
//
//   - Same display name, two folders → two different generated IDs.
//   - A display-name edit in the generated file leaves every ID byte
//     untouched.
//   - Live validation, refusals and a cancelled dialog never call the ID
//     source (call count 0 at the writer's injection seam); a real commit
//     draws exactly once.
//   - An ID that collides with an uncompiled generated file makes the
//     writer draw again.
//   - A domain reload and a Registry rediscovery leave the generated ID
//     byte-identical in the file, the attribute, the Descriptor and the
//     Registry, and the workflow reference to the hand-written
//     proj.transform.reset fixture still validates with the fixture
//     files byte-identical.
//   - The restart pair: Restart_GenerateAndRecord (armed by the
//     OPWEAVE_CREATE_ID_RESTART environment variable on an isolated
//     TestProject copy) records the generated ID and the written file
//     bytes under the copy's Library/; Restart_VerifyAcrossRestart in a
//     SECOND Editor process on the same copy compares them — and both
//     are Ignored, never passed or failed, in any ordinary
//     single-process run where no armed record exists, without
//     creating the restart folder or files at all.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.9-07:create-operation-uuid /
    /// local:T-W2.9-07:create-operation-id-restart — the SPEC §A4
    /// generated-ID stability tests.
    /// </summary>
    internal sealed class OperationCreateIdStabilityTests
    {
        private const string GeneratedIdPattern = "\\Aproj\\.op_[0-9a-f]{32}\\z";

        // Two folders for the same display name: StabilityA lands in the
        // predefined Editor assembly (namespace OpWeave.Custom), StabilityB
        // under the OpWeave.Custom asmdef's Transform subtree (namespace
        // OpWeave.Custom.StabilityB) — the generated classes can coexist.
        private const string TwinDisplayName = "Twin Probe";
        private const string TwinClassName = "TwinProbeOperation";
        private const string FolderA =
            "Assets/OpWeave/Editor/Operations/StabilityA";
        private const string FolderB =
            "Assets/OpWeave/Editor/Operations/Transform/StabilityB";
        private const string TwinFileA = FolderA + "/" + TwinClassName + ".cs";
        private const string TwinFileB = FolderB + "/" + TwinClassName + ".cs";

        // A twin request's generated test: neither twin folder carries
        // its own test host, so the planner falls back to the ordinally
        // first candidate — the fixture's OpWeave.Custom.Tests under
        // Transform/Tests, outside every folder cleanup deletes.
        private const string TwinTestFile =
            "Assets/OpWeave/Editor/Operations/Transform/Tests/" +
            TwinClassName + "Tests.cs";

        // An uncompiled .cs probe that only declares an ID: the
        // generated-file scan of SPEC §M.4 item 2 must collide with it.
        private const string FileProbeId = "proj.file.probe";
        private const string FileProbeFile =
            FolderA + "/FileProbeOperation.cs";

        // The restart probe: predefined Editor assembly, generated test
        // hosted by the fixture's OpWeave.Custom.Tests asmdef.
        private const string RestartDisplayName = "Restart Probe";
        private const string RestartClassName = "RestartProbeOperation";
        private const string RestartFolder =
            "Assets/OpWeave/Editor/Operations/RestartProbe";
        private const string RestartFile =
            RestartFolder + "/" + RestartClassName + ".cs";
        private const string RestartNamespace = "OpWeave.Custom";
        private const string RestartTestFile =
            "Assets/OpWeave/Editor/Operations/Transform/Tests/" +
            RestartClassName + "Tests.cs";

        // The hand-written custom-ID fixture of AT-01's shape: files and
        // workflow references must never be rewritten.
        private const string ExistingId = "proj.transform.reset";
        private const string ExistingOperationFile =
            "Assets/OpWeave/Editor/Operations/Transform/ResetTransformOperation.cs";
        private const string ExistingTestFile =
            "Assets/OpWeave/Editor/Operations/Transform/Tests/ResetTransformOperationTests.cs";

        // Process 1 of the restart pair arms the record with this
        // environment variable on its isolated TestProject copy.
        private const string ArmEnvVar = "OPWEAVE_CREATE_ID_RESTART";

        private const double SettleDeadlineSeconds = 240.0;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string Absolute(string assetRelative) =>
            Path.Combine(
                ProjectRoot,
                assetRelative.Replace('/', Path.DirectorySeparatorChar));

        private static string RecordDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveCreateIdRestart");
        private static string RecordFile => Path.Combine(RecordDir, "record.txt");
        private static string RecordFileDir => Path.Combine(RecordDir, "files");

        private static bool Armed =>
            string.Equals(
                Environment.GetEnvironmentVariable(ArmEnvVar), "1",
                StringComparison.Ordinal);

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        // The post-reload Registry poll keeps its state in statics that
        // are assigned only after the reload, and it runs as a
        // method-group predicate that captures nothing: UTF rebuilds
        // the test enumerator with only its program counter restored,
        // so a local captured by a lambda lives in a display class that
        // stays null in the new domain — the NRE the SessionState
        // carries were added against.
        private static string s_pollOperationId = string.Empty;
        private static RegisteredOperation? s_pollResult;

        private static bool PollResultMissing()
        {
            s_pollResult = PollRegistered(s_pollOperationId);
            return s_pollResult is null;
        }

        private static OperationCreateRequest NewRequest(
            string displayName, string targetFolder) =>
            new OperationCreateRequest
            {
                DisplayName = displayName,
                TargetFolder = targetFolder,
            };

        private static OperationCreateWriter.Result WritePlan(
            OperationCreateRequest request, Func<string>? idSource = null)
        {
            var plan = OperationCreatePlanner.Plan(request);
            if (!plan.CanWrite)
            {
                var refused = new OperationCreateWriter.Result();
                refused.Errors.Add(
                    "the plan was refused: " + string.Join("; ", plan.Issues));
                return refused;
            }
            return OperationCreateWriter.Write(plan, idSource);
        }

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

        // Deepest first, like OperationCreateMenuTests: deletes every path
        // this fixture can generate. Returns true when it removed an
        // imported script or asmdef — the only case that queues a
        // recompile + reload the caller must cross. An existing restart
        // record narrows the cleanup to the non-restart artifacts: the
        // record survives into the SECOND Editor process of the restart
        // pair, which re-runs the whole suite on the same copy, and the
        // recorded RestartFile/RestartTestFile — kept through their
        // RestartFolder parent — must still be there for
        // Restart_VerifyAcrossRestart. But the twin and file-probe
        // leftovers of the non-restart tests are still deleted: under
        // the whole-suite re-run a leftover would make the next
        // creation's plan collide on "already exists".
        private static bool CleanupProbeArtifacts()
        {
            var recordExists = File.Exists(RecordFile);
            var dirty = false;
            var scriptPending = false;
            foreach (var path in new[]
                { TwinFileA, TwinFileB, TwinTestFile, FileProbeFile,
                    RestartFile, RestartTestFile })
            {
                if (recordExists &&
                    (path == RestartFile || path == RestartTestFile))
                {
                    continue;
                }
                if (File.Exists(Absolute(path)))
                {
                    scriptPending |= IsImportedAsset(path);
                    DeleteAssetPath(path);
                    dirty = true;
                }
            }
            foreach (var folder in new[] { FolderA, FolderB, RestartFolder })
            {
                if (recordExists && folder == RestartFolder)
                {
                    continue;
                }
                if (FolderOrMetaExists(folder))
                {
                    scriptPending |= FolderHasImportedScript(folder);
                    DeleteFolderAsset(folder);
                    dirty = true;
                }
            }
            if (!recordExists && Directory.Exists(RecordDir))
            {
                Directory.Delete(RecordDir, recursive: true);
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
            }
            return scriptPending;
        }

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
                        "OperationCreateIdStabilityTests." + phase +
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
                            "OperationCreateIdStabilityTests." + m_phase +
                            " timed out after " + m_timeoutSec +
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
                            "OperationCreateIdStabilityTests." + m_phase +
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
                        "OperationCreateIdStabilityTests." + m_phase +
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
            SessionState.EraseString(SessionKey);
            SessionState.EraseString(WrittenHashSessionKey);
            SessionState.EraseString(ExistingOperationHashSessionKey);
            SessionState.EraseString(ExistingTestHashSessionKey);
            var scriptPending = CleanupProbeArtifacts();
            if (scriptPending)
            {
                yield return new BoundedScriptReload(
                    nameof(UnityTearDown), SettleDeadlineSeconds);
            }
            yield return WaitForEditorIdle(nameof(UnityTearDown));
        }

        // ── SPEC §A4 test (ID): same display name → different IDs ───────

        // SPEC §A4 test (ID): two creations with the same display name in
        // different folders get different generated UUID IDs. The display
        // name feeds the class/file names, never the ID.
        [Test]
        public void Write_SameDisplayNameTwoFolders_GeneratesDifferentIds()
        {
            var first = WritePlan(NewRequest(TwinDisplayName, FolderA));
            Assert.That(first.Ok, Is.True,
                "first write failed: " + string.Join("; ", first.Errors));
            Assert.That(first.OperationId, Does.Match(GeneratedIdPattern));

            // The second creation asks for no test file: FolderB has no
            // test host of its own either, so it would plan the same
            // Transform/Tests/TwinProbeOperationTests.cs the first
            // creation already wrote and be refused on "already exists".
            // The SPEC §A4 criterion is the two distinct generated IDs,
            // which disabling the second generated test does not touch.
            var secondRequest = NewRequest(TwinDisplayName, FolderB);
            secondRequest.CreateTests = false;
            var second = WritePlan(secondRequest);
            Assert.That(second.Ok, Is.True,
                "second write failed: " + string.Join("; ", second.Errors));
            Assert.That(second.OperationId, Does.Match(GeneratedIdPattern));
            Assert.That(second.OperationId, Is.Not.EqualTo(first.OperationId),
                "two creations with the same display name must draw " +
                "different UUIDs");
            Assert.That(File.ReadAllText(Absolute(TwinFileA)),
                Does.Contain("\"" + first.OperationId + "\""));
            Assert.That(File.ReadAllText(Absolute(TwinFileB)),
                Does.Contain("\"" + second.OperationId + "\""));
        }

        // SPEC §A4 test (ID): a display-name edit of the generated file —
        // the only edit a user can make to the generated Descriptor —
        // changes no ID byte. The ID is independent of the display name.
        [Test]
        public void Write_DisplayNameEdit_KeepsGeneratedId()
        {
            var write = WritePlan(NewRequest(TwinDisplayName, FolderA));
            Assert.That(write.Ok, Is.True,
                "write failed: " + string.Join("; ", write.Errors));
            var operationId = write.OperationId!;
            var before = File.ReadAllText(Absolute(TwinFileA));
            Assert.That(before,
                Does.Contain("DisplayName = \"" + TwinDisplayName + "\""));

            var after = before.Replace(
                "DisplayName = \"" + TwinDisplayName + "\"",
                "DisplayName = \"Twin Probe Renamed\"");
            Assert.That(after, Is.Not.EqualTo(before),
                "the display-name edit must change the file");
            File.WriteAllText(Absolute(TwinFileA), after);
            AssetDatabase.ImportAsset(TwinFileA);

            var edited = File.ReadAllText(Absolute(TwinFileA));
            Assert.That(edited, Does.Contain("\"" + operationId + "\""),
                "the generated ID is unchanged by the display-name edit");
            Assert.That(edited,
                Does.Contain("[OpWeaveOperation(\"" + operationId + "\", 1)]"));
            Assert.That(edited,
                Does.Contain(".Define(\"" + operationId + "\", 1)"));
        }

        // ── Generation only at commit ──────────────────────────────────

        // SPEC §M.4 item 2: redraws, previews, live validation and a
        // cancelled dialog never mint an ID. The only path that can call
        // the ID source is the writer's commit, so the injected counting
        // source proves the boundary: three live-validation replans and
        // two refused writes (the cancelled/failed dialog outcomes) leave
        // the call count at 0; one real commit draws exactly once.
        [Test]
        public void ValidateAndCancel_NeverCallTheIdSource()
        {
            var calls = 0;
            string Source()
            {
                calls++;
                return OperationIdGenerator.NewUuidV4Id();
            }

            // Redraws / previews / validation passes: repeated Plan calls.
            for (var i = 0; i < 3; i++)
            {
                var plan = OperationCreatePlanner.Plan(
                    NewRequest(TwinDisplayName, FolderA));
                Assert.That(plan.CanWrite, Is.True,
                    "unexpected issues: " + string.Join("; ", plan.Issues));
            }
            Assert.That(calls, Is.EqualTo(0),
                "live validation must never call the ID source");
            Assert.IsFalse(File.Exists(Absolute(TwinFileA)),
                "validation alone writes nothing");

            // A refused plan — a validation failure or the dialog's
            // cancelled/failed outcomes — is rejected before the draw.
            var badRequest = NewRequest(TwinDisplayName, FolderA);
            badRequest.Version = 0;
            var badPlan = OperationCreatePlanner.Plan(badRequest);
            Assert.That(badPlan.CanWrite, Is.False);
            var refused = OperationCreateWriter.Write(badPlan, Source);
            Assert.That(refused.Ok, Is.False);
            Assert.That(calls, Is.EqualTo(0),
                "a refused write must not draw an ID");

            // One real commit draws the source exactly once.
            var committed = WritePlan(
                NewRequest(TwinDisplayName, FolderA), Source);
            Assert.That(committed.Ok, Is.True,
                "commit failed: " + string.Join("; ", committed.Errors));
            Assert.That(calls, Is.EqualTo(1),
                "one commit must draw the ID source exactly once");
            Assert.That(committed.OperationId, Does.Match(GeneratedIdPattern));
        }

        // ── Commit-time collision checks ───────────────────────────────

        // SPEC §M.4 item 2: the generated-file scan catches an ID declared
        // in an existing file under the watched folders even before it
        // compiles or registers — the first draw collides, the second is
        // accepted and written once. Both legal spellings of the
        // attribute, [OpWeaveOperation( and [OpWeaveOperationAttribute(,
        // must collide.
        [TestCase("OpWeaveOperation")]
        [TestCase("OpWeaveOperationAttribute")]
        public void Write_IdDeclaredInExistingFile_Redraws(string attributeName)
        {
            var collisionId = FileProbeId;
            Directory.CreateDirectory(
                Path.GetDirectoryName(Absolute(FileProbeFile))!);
            File.WriteAllText(
                Absolute(FileProbeFile),
                "using OpWeave.Core.Operations;\n" +
                "[" + attributeName + "(\"" + collisionId + "\", 1)]\n" +
                "internal sealed class FileProbeOperation { }\n");
            try
            {
                var calls = 0;
                var committed = WritePlan(
                    NewRequest(TwinDisplayName, FolderA),
                    () => calls++ == 0
                        ? collisionId
                        : "proj.op_0123456789abcdef0123456789abcdef");
                Assert.That(committed.Ok, Is.True,
                    "write failed: " + string.Join("; ", committed.Errors));
                Assert.That(calls, Is.EqualTo(2),
                    "a file-declared collision must redraw once");
                Assert.That(committed.OperationId,
                    Is.EqualTo("proj.op_0123456789abcdef0123456789abcdef"));
                Assert.That(File.ReadAllText(Absolute(TwinFileA)),
                    Does.Contain(
                        "\"proj.op_0123456789abcdef0123456789abcdef\""));
            }
            finally
            {
                DeleteAssetPath(FileProbeFile);
            }
        }

        // ── Stability across reload and Registry rediscovery ───────────

        // SPEC §A4 test (ID): the generated ID survives the domain reload
        // the write itself triggers and a forced Registry rediscovery —
        // byte-identical in the generated file, attribute, Descriptor and
        // Registry. The hand-written proj.transform.reset fixture's files
        // and a workflow node referencing it stay byte-identical and valid.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Write_ReloadAndRediscovery_KeepGeneratedId()
        {
            var existingOperationBytes =
                File.ReadAllBytes(Absolute(ExistingOperationFile));
            var existingTestBytes =
                File.ReadAllBytes(Absolute(ExistingTestFile));

            var write = WritePlan(NewRequest(TwinDisplayName, FolderA));
            Assert.That(write.Ok, Is.True,
                "write failed: " + string.Join("; ", write.Errors));
            var operationId = write.OperationId!;
            Assert.That(operationId, Does.Match(GeneratedIdPattern));
            var writtenBytes = File.ReadAllBytes(Absolute(TwinFileA));
            // UTF does not restore enumerator locals across the domain
            // reload below: every value the post-reload phase needs is
            // carried in SessionState — the generated ID itself and the
            // SHA-256 of each file the byte-identical asserts compare.
            SessionState.SetString(SessionKey, operationId);
            SessionState.SetString(WrittenHashSessionKey,
                Sha256Hex(writtenBytes));
            SessionState.SetString(ExistingOperationHashSessionKey,
                Sha256Hex(existingOperationBytes));
            SessionState.SetString(ExistingTestHashSessionKey,
                Sha256Hex(existingTestBytes));

            ArmDomainSentinel();
            yield return new BoundedScriptReload(
                nameof(Write_ReloadAndRediscovery_KeepGeneratedId),
                SettleDeadlineSeconds);
            yield return WaitWhileBusy(
                nameof(Write_ReloadAndRediscovery_KeepGeneratedId) +
                    ".DomainReload",
                () => DomainSentinelAlive,
                SettleDeadlineSeconds,
                "the domain sentinel — the generated write's " +
                    "domain reload");
            Assert.IsFalse(DomainSentinelAlive,
                "the generated write did not produce a domain reload");

            var persistedId = SessionState.GetString(SessionKey, string.Empty);
            var persistedWrittenHash = SessionState.GetString(
                WrittenHashSessionKey, string.Empty);
            var persistedExistingOperationHash = SessionState.GetString(
                ExistingOperationHashSessionKey, string.Empty);
            var persistedExistingTestHash = SessionState.GetString(
                ExistingTestHashSessionKey, string.Empty);
            Assert.That(persistedId, Does.Match(GeneratedIdPattern),
                "the generated ID must be unchanged by the domain reload");
            Assert.That(persistedWrittenHash, Is.Not.Empty,
                "the written-file hash must survive the domain reload");
            Assert.That(persistedExistingOperationHash, Is.Not.Empty,
                "the existing Operation file hash must survive the " +
                "domain reload");
            Assert.That(persistedExistingTestHash, Is.Not.Empty,
                "the existing test file hash must survive the " +
                "domain reload");

            // After the reload: the file is byte-identical and the
            // generated class is discovered into the Registry under the
            // same ID.
            Assert.That(Sha256Hex(File.ReadAllBytes(Absolute(TwinFileA))),
                Is.EqualTo(persistedWrittenHash),
                "the generated file must be byte-identical after the " +
                "domain reload");
            s_pollOperationId = persistedId;
            s_pollResult = null;
            yield return WaitWhileBusy(
                nameof(Write_ReloadAndRediscovery_KeepGeneratedId) +
                    ".Registry",
                PollResultMissing,
                SettleDeadlineSeconds,
                "'" + persistedId + "' to register after the reload");
            Assert.That(s_pollResult, Is.Not.Null,
                "'" + persistedId + "' was not registered after the reload");

            // A forced Registry rediscovery rebuilds the snapshot; the ID
            // is not rewritten.
            RegistryBuildHost.RebuildProduction();
            var snapshot = RegistryPublication.Shared.Snapshot;
            Assert.That(snapshot, Is.Not.Null);
            var rediscovered = snapshot!.Find(persistedId, 1);
            Assert.That(rediscovered, Is.Not.Null,
                "'" + persistedId + "' lost its registration across the " +
                "Registry rediscovery");
            Assert.That(rediscovered!.Descriptor.Id, Is.EqualTo(persistedId));
            Assert.That(Sha256Hex(File.ReadAllBytes(Absolute(TwinFileA))),
                Is.EqualTo(persistedWrittenHash),
                "the generated file must be byte-identical after the " +
                "Registry rediscovery");

            var type = FindType(RestartNamespace + "." + TwinClassName);
            Assert.That(type, Is.Not.Null,
                "the generated class did not compile into the domain");
            var declared = OperationEligibility.DeclaredIdentityOf(type!);
            Assert.That(declared, Is.Not.Null);
            Assert.That(declared!.Id, Is.EqualTo(persistedId),
                "attribute ID must equal the registered Descriptor ID");

            // Existing workflow reference: a WF node bound to the
            // hand-written fixture still validates, and the fixture's own
            // files are byte-identical — nothing rewrites existing IDs.
            Assert.That(snapshot.Find(ExistingId, 1), Is.Not.Null,
                "the hand-written fixture must still be registered");
            var document = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "stability.references",
                Nodes = new List<NodeData>
                {
                    new NodeData
                    {
                        Id = "reset",
                        Operation = ExistingId,
                        Version = 1,
                    },
                },
                ControlEdges = new List<ControlEdge>(),
            });
            Assert.That(document.Ok, Is.True);
            foreach (var diagnostic in IdGrammar.Validate(document.Value!))
            {
                Assert.That(diagnostic.Code, Is.Not.EqualTo("OW-WF-005"),
                    "the existing workflow reference became invalid: " +
                    diagnostic.Code + " " + diagnostic.Message);
            }
            Assert.That(
                Sha256Hex(File.ReadAllBytes(Absolute(ExistingOperationFile))),
                Is.EqualTo(persistedExistingOperationHash),
                "the existing Operation file must not be rewritten");
            Assert.That(
                Sha256Hex(File.ReadAllBytes(Absolute(ExistingTestFile))),
                Is.EqualTo(persistedExistingTestHash),
                "the existing test file must not be rewritten");
        }

        private const string SessionKey = "OpWeave.StabilityTests.GeneratedId";
        private const string WrittenHashSessionKey =
            "OpWeave.StabilityTests.WrittenSha256";
        private const string ExistingOperationHashSessionKey =
            "OpWeave.StabilityTests.ExistingOperationSha256";
        private const string ExistingTestHashSessionKey =
            "OpWeave.StabilityTests.ExistingTestSha256";

        // SHA-256 hex: byte arrays cannot ride in SessionState, so the
        // post-reload byte-identical asserts compare file hashes.
        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var builder = new System.Text.StringBuilder(64);
                foreach (var b in sha.ComputeHash(bytes))
                {
                    builder.Append(b.ToString(
                        "x2",
                        System.Globalization.CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        // One discovery poll — the caller yields between polls so the
        // Editor's rebuild work can progress.
        private static RegisteredOperation? PollRegistered(string operationId)
        {
            var snapshot = RegistryPublication.Shared.Snapshot;
            return snapshot is not null && !RegistryPublication.Shared.Stale
                ? snapshot.Find(operationId, 1)
                : null;
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

        // ── local:T-W2.9-07:create-operation-id-restart ─────────────────

        // Process 1 of the restart pair, run by Opus on an isolated
        // TestProject copy with OPWEAVE_CREATE_ID_RESTART=1: creates the
        // Operation through the menu path (the same Plan → Write the
        // dialog's Create button runs) and records the generated ID plus
        // a byte-for-byte copy of every written file under the copy's
        // Library/. Without the arm variable — any ordinary suite run —
        // the test reports Ignored before creating anything: the restart
        // folder and files are never created outside the armed run, and
        // the verify test sees no record (Ignored).
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Restart_GenerateAndRecord()
        {
            if (!Armed)
            {
                Assert.Ignore(
                    "OPWEAVE_CREATE_ID_RESTART is not set — the restart " +
                    "pair runs only on the isolated TestProject copy; " +
                    "an unarmed run creates no restart files");
            }

            // Process 2 of the pair re-runs the whole suite on the same
            // copy: a complete record already on disk means this Editor
            // is the verifier, and a fresh create would only fail on the
            // recorded files it must not touch.
            var existing = ReadRecord();
            if (existing is not null &&
                existing.Files.TrueForAll(f => File.Exists(Absolute(f))))
            {
                Assert.Ignore(
                    "an armed restart record already exists; this " +
                    "Editor process verifies it instead of re-creating");
            }

            var write = WritePlan(NewRequest(RestartDisplayName, RestartFolder));
            Assert.That(write.Ok, Is.True,
                "the generated write failed: " +
                string.Join("; ", write.Errors));
            var operationId = write.OperationId!;
            Assert.That(operationId, Does.Match(GeneratedIdPattern));
            Assert.That(write.WrittenAssets, Does.Contain(RestartFile));
            Assert.That(write.WrittenAssets, Does.Contain(RestartTestFile));

            WriteRecord(operationId, write.WrittenAssets);
            var record = ReadRecord();
            Assert.That(record, Is.Not.Null, "the record must round-trip");
            Assert.That(record!.OperationId, Is.EqualTo(operationId));

            // The write queued a script compile + reload; cross it here,
            // inside the same [UnityTest] that produced it and through
            // the bounded instruction, so no pending reload is handed to
            // the next fixture.
            yield return new BoundedScriptReload(
                nameof(Restart_GenerateAndRecord), SettleDeadlineSeconds);
            yield return WaitForEditorIdle(nameof(Restart_GenerateAndRecord));
        }

        private sealed class Record
        {
            internal string OperationId = string.Empty;
            internal int WriterPid;
            internal readonly List<string> Files = new List<string>();
        }

        // record.txt: "id=<id>", "pid=<process id>", then one
        // "file=<asset path>" per written asset; files/NN.bin holds each
        // file's bytes in manifest order.
        private static void WriteRecord(
            string operationId, IReadOnlyList<string> writtenAssets)
        {
            Directory.CreateDirectory(RecordFileDir);
            var lines = new List<string>
            {
                "id=" + operationId,
                "pid=" + System.Diagnostics.Process
                    .GetCurrentProcess().Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
            };
            for (var i = 0; i < writtenAssets.Count; i++)
            {
                File.WriteAllBytes(
                    Path.Combine(RecordFileDir, i.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                        ".bin"),
                    File.ReadAllBytes(Absolute(writtenAssets[i])));
                lines.Add("file=" + writtenAssets[i]);
            }
            File.WriteAllLines(RecordFile, lines);
        }

        private static Record? ReadRecord()
        {
            if (!File.Exists(RecordFile))
            {
                return null;
            }
            var record = new Record();
            foreach (var line in File.ReadAllLines(RecordFile))
            {
                if (line.StartsWith("id=", StringComparison.Ordinal))
                {
                    record.OperationId = line.Substring(3);
                }
                else if (line.StartsWith("pid=", StringComparison.Ordinal) &&
                    int.TryParse(line.Substring(4), out var pid))
                {
                    record.WriterPid = pid;
                }
                else if (line.StartsWith("file=", StringComparison.Ordinal))
                {
                    record.Files.Add(line.Substring(5));
                }
            }
            return record.OperationId.Length == 0 ? null : record;
        }

        // Process 2 of the restart pair — a new Editor process on the same
        // isolated copy. In every other situation (no armed process 1, a
        // record written by this very Editor process in the same suite
        // run, or a stale record whose files the suite cleanup removed)
        // it reports Ignored — never pass — per the ticket's documented
        // single-process outcome.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Restart_VerifyAcrossRestart()
        {
            var record = ReadRecord();
            if (record is null)
            {
                Assert.Ignore(
                    "no recorded ID: process 1 of the two-process run " +
                    "(OPWEAVE_CREATE_ID_RESTART=1) has not run");
            }
            if (record!.WriterPid ==
                System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                Assert.Ignore(
                    "the record was written by this Editor process; " +
                    "a second process is required");
            }
            for (var i = 0; i < record.Files.Count; i++)
            {
                if (!File.Exists(Absolute(record.Files[i])))
                {
                    Assert.Ignore(
                        "the recorded file '" + record.Files[i] +
                        "' no longer exists — a stale record, not a " +
                        "restart pair");
                }
            }

            // Wait for the isolated copy's first compile + discovery pass
            // to register the generated Operation.
            yield return WaitForEditorIdle(
                nameof(Restart_VerifyAcrossRestart));
            RegisteredOperation? found = null;
            yield return WaitWhileBusy(
                nameof(Restart_VerifyAcrossRestart) + ".Registry",
                () => (found = PollRegistered(record.OperationId)) is null,
                SettleDeadlineSeconds,
                "'" + record.OperationId + "' to register in the " +
                    "second Editor process");
            Assert.That(found, Is.Not.Null,
                "'" + record.OperationId + "' was not registered in the " +
                "second Editor process");

            // The ID equals the recorded one in all four places: the
            // Registry Descriptor, the compiled attribute, the generated
            // test file and the generated Operation file.
            Assert.That(found!.Descriptor.Id, Is.EqualTo(record.OperationId));
            Assert.That(found.Descriptor.Version, Is.EqualTo(1));
            var type = FindType(RestartNamespace + "." + RestartClassName);
            Assert.That(type, Is.Not.Null,
                "the generated class did not compile into the new domain");
            var declared = OperationEligibility.DeclaredIdentityOf(type!);
            Assert.That(declared, Is.Not.Null);
            Assert.That(declared!.Id, Is.EqualTo(record.OperationId),
                "the attribute ID must equal the recorded ID across " +
                "the restart");
            Assert.That(File.ReadAllText(Absolute(RestartTestFile)),
                Does.Contain("\"" + record.OperationId + "\""),
                "the generated test must carry the recorded ID");

            // Byte-identical: every file process 1 wrote is unchanged.
            for (var i = 0; i < record.Files.Count; i++)
            {
                var recorded = File.ReadAllBytes(
                    Path.Combine(RecordFileDir, i.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) +
                        ".bin"));
                Assert.That(File.ReadAllBytes(Absolute(record.Files[i])),
                    Is.EqualTo(recorded),
                    "'" + record.Files[i] + "' changed across the restart");
            }
        }
    }
}
