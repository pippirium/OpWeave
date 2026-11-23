#nullable enable

// T-W2.7-02 [Edit] tests: local:T-W2.7-02:asset-import-wait for the
// unity.asset.import step itself — the path/file preconditions, the
// Execute→Waiting hand-off, the Poll outcomes over the §G4 condition, and
// the live import: a real text file completes after the asynchronous
// ImportAsset(ForceUpdate), and a file whose content the importer rejects
// ends in OW-UNITY-008 after three null loads while not updating. The
// seam-driven cases run in one frame; the live cases are [UnityTest]s that
// yield between polls because the queued import completes on the Editor
// update, not inside a test call.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Per-step behaviour of <c>unity.asset.import</c> (SPEC §I.3, §G7 item 2).
    /// </summary>
    internal sealed class AssetImportOperationTests
    {
        private const string ProbeFolder = "Assets/OpWeaveAssetImport";
        private const string TextPath = ProbeFolder + "/probe.txt";
        private const string BrokenPath = ProbeFolder + "/broken.asset";

        private static readonly IReadOnlyDictionary<string, string> Ports =
            new Dictionary<string, string> { [ImportAssetOperation.PathPort] = "unity.AssetPath" };

        private static readonly IReadOnlyDictionary<string, string> OutPorts =
            new Dictionary<string, string>
            { [ImportAssetOperation.AssetPort] = "unity.AssetRef<unity.Object>" };

        [SetUp]
        public void SetUp()
        {
            EnsureFolder(ProbeFolder);
        }

        [TearDown]
        public void TearDown()
        {
            ImportErrorCapture.Clear();
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            AssetDatabase.Refresh();
        }

        // ── preconditions ────────────────────────────────────────────────

        // §I.3 "ファイルがディスクにあること": a missing file fails before
        // any import is queued — OW-UNITY-008, the §J import-failure code.
        [Test]
        public void Execute_FileMissing_FailsWithOwUnity008AndImportsNothing()
        {
            var imported = new List<string>();
            var op = new ImportAssetOperation(
                imported.Add, _ => false, _ => null, NeverFinishes.Instance);

            var result = op.Execute(Context(TextPath));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_008, ImportAssetOperation.PathPort);
            Assert.That(imported, Is.Empty, "a refused path must not queue an import");
        }

        [Test]
        public void Execute_EscapingPath_FailsWithOwPerm002AndImportsNothing()
        {
            var imported = new List<string>();
            var op = new ImportAssetOperation(
                imported.Add, _ => true, _ => null, NeverFinishes.Instance);

            var result = op.Execute(Context("Assets/../escape.txt"));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, ImportAssetOperation.PathPort);
            Assert.That(imported, Is.Empty);
        }

        // A Dry Run hears the same refusal from Preflight without any change.
        [Test]
        public void Preflight_MissingFile_ReportsOwUnity008WithoutImporting()
        {
            var imported = new List<string>();
            var op = new ImportAssetOperation(
                imported.Add, _ => false, _ => null, NeverFinishes.Instance);

            var preflight = op.Preflight(Context(TextPath));

            Assert.That(preflight.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(preflight.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_UNITY_008));
            Assert.That(imported, Is.Empty);
        }

        // ── the Waitable hand-off ────────────────────────────────────────

        [Test]
        public void Execute_ExistingFile_QueuesTheImportAndWaits()
        {
            var imported = new List<string>();
            var op = new ImportAssetOperation(
                imported.Add, _ => true, _ => null, NeverFinishes.Instance);

            var result = op.Execute(Context(TextPath));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting), () => Describe(result));
            Assert.That(imported, Is.EqualTo(new[] { TextPath }),
                "ImportAsset is queued on the normalized path, once");
            Assert.That(result.WaitingFor, Is.EqualTo(ImportAssetOperation.WaitingForLabel));
            var token = result.ResumeToken!.ToToken();
            Assert.That(token["paths"]!.ToObject<string[]>(), Is.EqualTo(new[] { TextPath }));
            Assert.That((int?)token["nulls"], Is.EqualTo(0));
        }

        [Test]
        public void Poll_PendingCarriesTheUpdatedTokenBack()
        {
            var updated = AssetImportedCondition.InitialToken(new[] { TextPath });
            var op = new ImportAssetOperation(
                _ => { }, _ => true, _ => null,
                new ScriptedCondition(WaitStatus.Pending(updated)));

            var result = op.Poll(Context(TextPath, resumeToken: Token()));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting));
            Assert.That(result.ResumeToken, Is.SameAs(updated),
                "the condition's rewritten token is what the next Poll must see");
        }

        [Test]
        public void Poll_FailedPropagatesTheConditionDiagnostic()
        {
            var failure = ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008, "The asset import did not produce a loadable asset.",
                hint: "Import errors: boom");
            var op = new ImportAssetOperation(
                _ => { }, _ => true, _ => null,
                new ScriptedCondition(WaitStatus.Failed(failure)));

            var result = op.Poll(Context(TextPath, resumeToken: Token()));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error!.Code, Is.EqualTo(ErrorCodes.OW_UNITY_008));
            Assert.That(result.Error.Port, Is.EqualTo(ImportAssetOperation.PathPort),
                "the step names the port the failed import belongs to");
        }

        [Test]
        public void Poll_DoneSetsTheAssetOutput()
        {
            var asset = CreateTextAsset(TextPath);
            var op = new ImportAssetOperation(
                _ => { }, _ => true,
                AssetDatabase.LoadMainAssetAtPath,
                new ScriptedCondition(WaitStatus.Done()));

            var context = Context(TextPath, resumeToken: Token());
            var result = op.Poll(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var emitted = (JObject)context.Outputs[ImportAssetOperation.AssetPort].ToToken();
            Assert.That((string?)emitted["kind"], Is.EqualTo("asset"));
            Assert.That((string?)emitted["path"], Is.EqualTo(TextPath));
            Assert.That((string?)emitted["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(TextPath)));
        }

        // ── the step's import errors (SPEC §G4) ─────────────────────────

        // An entry the capture logged before the step began — an earlier
        // failed import of the same path is still in the ring — is not the
        // step's error: Execute opens the attribution boundary before the
        // import is queued, so it reaches neither the token nor the
        // OW-UNITY-008 hint.
        [Test]
        public void Execute_DropsCaptureEntriesThatPredateTheStep()
        {
            ImportErrorCapture.Clear();
            try
            {
                ImportErrorCapture.OnLogMessage(
                    "Asset import failed for '" + TextPath + "' - earlier failure",
                    LogType.Error);
                var op = new ImportAssetOperation(
                    _ => { }, _ => true, _ => null, NeverFinishes.Instance);

                var result = op.Execute(Context(TextPath));

                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting),
                    () => Describe(result));
                var folded = (JArray)result.ResumeToken!.ToToken()["errors"]!;
                Assert.That(folded, Is.Empty,
                    "an error logged before the step began is not attributed to it");

                // The live condition drains the same bounded capture: the
                // stale entry still does not reach the token.
                var status = new AssetImportedCondition().Check(result.ResumeToken!);
                var drained = (JArray)status.ResumeToken!.ToToken()["errors"]!;
                Assert.That(drained, Is.Empty);
            }
            finally
            {
                ImportErrorCapture.Clear();
            }
        }

        // The queued import can report its failure before the first Poll —
        // and only the resume token survives a domain reload — so Execute
        // folds the errors ImportAsset logged into the token it returns.
        [Test]
        public void Execute_FoldsTheImportsOwnErrorsIntoTheToken()
        {
            ImportErrorCapture.Clear();
            try
            {
                var op = new ImportAssetOperation(
                    path => ImportErrorCapture.OnLogMessage(
                        "Asset import failed for '" + path + "' - bad bytes",
                        LogType.Error),
                    _ => true, _ => null, NeverFinishes.Instance);

                var result = op.Execute(Context(TextPath));

                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting),
                    () => Describe(result));
                var folded = (JArray)result.ResumeToken!.ToToken()["errors"]!;
                Assert.That(folded, Has.Count.EqualTo(1));
                Assert.That((string?)folded[0], Does.Contain("bad bytes"));
            }
            finally
            {
                ImportErrorCapture.Clear();
            }
        }

        // SPEC §G4 "ステップ中のインポートエラーを添付" across a domain
        // reload: an error the import logs only after Execute returned the
        // initial resume token is persisted to SessionState as it is
        // captured, so a reload before the next Poll cannot lose it — the
        // eventual OW-UNITY-008 still carries it, and the finished step's
        // capture slot is dropped.
        [Test]
        public void Poll_ErrorLoggedAfterToken_SurvivesDomainReload()
        {
            ImportErrorCapture.Clear();
            try
            {
                var condition = new AssetImportedCondition(
                    () => false, _ => null,
                    () => ImportErrorCapture.RecentErrors);
                var op = new ImportAssetOperation(
                    _ => { }, _ => true, _ => null, condition);

                var result = op.Execute(Context(TextPath));
                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting),
                    () => Describe(result));
                var token = result.ResumeToken!;

                // The late error: the import reports its failure only after
                // the initial token was already snapshotted.
                ImportErrorCapture.OnLogMessage(
                    "Asset import failed for '" + TextPath + "' - late error",
                    LogType.Error);
                // The domain reload before the next Poll drops the
                // capture's domain-local mirror.
                ImportErrorCapture.SimulateDomainReload();

                result = op.Poll(Context(TextPath, resumeToken: token));
                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting),
                    () => Describe(result));
                var folded = (JArray)result.ResumeToken!.ToToken()["errors"]!;
                Assert.That(folded, Has.Count.EqualTo(1),
                    "the error captured before the reload is folded into " +
                    "the token after it");
                Assert.That((string?)folded[0], Does.Contain("late error"));

                for (var i = 1;
                    i < AssetImportedCondition.MaxConsecutiveNulls;
                    i++)
                {
                    result = op.Poll(
                        Context(TextPath, resumeToken: result.ResumeToken!));
                }
                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed),
                    () => Describe(result));
                Assert.That(result.Error!.Code,
                    Is.EqualTo(ErrorCodes.OW_UNITY_008));
                Assert.That(result.Error.Hint, Does.Contain("late error"));
                Assert.That(ImportErrorCapture.RecentErrors, Is.Empty,
                    "the finished step's capture slot is dropped");
            }
            finally
            {
                ImportErrorCapture.Clear();
            }
        }

        // The restart half of the same rule — the failure this ticket was
        // corrected on: an Editor RESTART, not a domain reload, so every
        // byte of SessionState is discarded, not just the capture's
        // domain-local mirror. An import error logged only after the last
        // persisted StepWaiting token is journaled into the step's §F6
        // RunLog while the Run executes — the channel a restart cannot
        // lose — and the recovered wait's OW-UNITY-008 still attaches it.
        [Test]
        public void Poll_ErrorLoggedAfterLastToken_SurvivesEditorSessionLoss()
        {
            var storeRoot = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library",
                "OpWeaveAssetImportRestart",
                "t" + Guid.NewGuid().ToString("N"));
            var evidence =
                "Asset import failed for '" + BrokenPath + "' - restart evidence";
            ImportErrorCapture.Clear();
            var ignore = LogAssert.ignoreFailingMessages;
            RunnerHostFixture? first = null;
            RunnerHostFixture? second = null;
            try
            {
                // Two checks in, the persisted StepWaiting carries
                // {nulls:2} and no captured errors — the resume token the
                // restart restores the waiting step from.
                first = new RunnerHostFixture(storeRoot);
                RegisterImport(first);
                var runId = AcceptImportWait(first);
                PollTicks(first, 2);
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        storeRoot, runId,
                        "\"type\":\"step_waiting\"", "\"nulls\":2"),
                    "the last persisted token must precede the late import error");

                // The late import error: Console-reported while the
                // waiting step is still the Run's latest attempt — the
                // §F6 capture journals it on the step — then the whole
                // Editor session state is discarded and the store is
                // handed to a new host (a new session id, the §F4 item 4
                // restart detection). FlushJournal is the quitting-time
                // write of the Buffered record (SPEC §F3 item 1).
                LogAssert.ignoreFailingMessages = true;
                Debug.LogError(evidence);
                Assert.IsTrue(
                    first.Host.FlushJournal().Ok,
                    "the quitting-time flush must persist the buffered log record");
                ImportErrorCapture.Clear();
                first.Dispose();
                first = null;

                second = new RunnerHostFixture(storeRoot);
                RegisterImport(second);
                var recovered = second.Host.RecoverRuns();
                Assert.That(
                    recovered.Ok, Is.True,
                    () => DescribeDiagnostics(recovered.Diagnostics));
                second.Clock.Advance(TimeSpan.FromMilliseconds(150));
                var tick = second.Host.Tick();
                Assert.That(
                    tick.Ok, Is.True,
                    () => DescribeDiagnostics(tick.Diagnostics));
                Assert.That(
                    second.Host.Coordinator.StateOf(runId),
                    Is.EqualTo(RunState.Failed),
                    "the restored wait's third consecutive null ends it");

                Diagnostic? failure = null;
                foreach (var diagnostic in tick.Diagnostics)
                {
                    if (diagnostic.Code == ErrorCodes.OW_UNITY_008)
                    {
                        failure = diagnostic;
                    }
                }
                Assert.That(
                    failure, Is.Not.Null,
                    "the restored wait must fail with OW-UNITY-008");
                Assert.That(
                    failure!.Hint, Does.Contain("restart evidence"),
                    "the import error journaled after the last token " +
                    "must still be attached");
            }
            finally
            {
                second?.Dispose();
                first?.Dispose();
                LogAssert.ignoreFailingMessages = ignore;
                ImportErrorCapture.Clear();
                if (Directory.Exists(storeRoot))
                {
                    try { Directory.Delete(storeRoot, recursive: true); }
                    catch (Exception) { /* a held file stays for the next run */ }
                }
            }
        }

        // The abrupt-termination half of the same rule — the residual hole
        // the follow-up review found: an Editor that dies between the error
        // and the next lifecycle event runs neither beforeAssemblyReload
        // nor quitting, so the Buffered §F6 record is lost with the
        // SessionState mirror. The §F6 capture must therefore make a
        // step-attributed import error durable at append time: this test
        // reads the journal file back WITHOUT any FlushJournal or lifecycle
        // hook — the record must already be there — and the recovered wait
        // still attaches it.
        [Test]
        public void Poll_ErrorLoggedAfterLastToken_SurvivesAbruptSessionLoss()
        {
            var storeRoot = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library",
                "OpWeaveAssetImportRestart",
                "t" + Guid.NewGuid().ToString("N"));
            var evidence =
                "Asset import failed for '" + BrokenPath + "' - abrupt evidence";
            ImportErrorCapture.Clear();
            var ignore = LogAssert.ignoreFailingMessages;
            RunnerHostFixture? first = null;
            RunnerHostFixture? second = null;
            try
            {
                // Two checks in, the persisted StepWaiting carries
                // {nulls:2} and no captured errors — the resume token the
                // abrupt loss restores the waiting step from.
                first = new RunnerHostFixture(storeRoot);
                RegisterImport(first);
                var runId = AcceptImportWait(first);
                PollTicks(first, 2);
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        storeRoot, runId,
                        "\"type\":\"step_waiting\"", "\"nulls\":2"),
                    "the last persisted token must precede the late import error");

                // The late import error: Console-reported while the
                // waiting step is still the Run's latest attempt. The §F6
                // capture journals it on the step and the waiting
                // capture's bound flush makes the Buffered record durable
                // immediately — NO FlushJournal and no lifecycle hook is
                // called, so the journal file must already carry it, which
                // is exactly the state an abrupt termination leaves.
                LogAssert.ignoreFailingMessages = true;
                Debug.LogError(evidence);
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        storeRoot, runId,
                        "\"type\":\"log\"", "\"unity_console\"",
                        evidence),
                    "a step-attributed import error must reach the journal " +
                    "file without waiting for a lifecycle flush");
                ImportErrorCapture.Clear();
                first.Dispose();
                first = null;

                second = new RunnerHostFixture(storeRoot);
                RegisterImport(second);
                var recovered = second.Host.RecoverRuns();
                Assert.That(
                    recovered.Ok, Is.True,
                    () => DescribeDiagnostics(recovered.Diagnostics));
                second.Clock.Advance(TimeSpan.FromMilliseconds(150));
                var tick = second.Host.Tick();
                Assert.That(
                    tick.Ok, Is.True,
                    () => DescribeDiagnostics(tick.Diagnostics));
                Assert.That(
                    second.Host.Coordinator.StateOf(runId),
                    Is.EqualTo(RunState.Failed),
                    "the restored wait's third consecutive null ends it");

                Diagnostic? failure = null;
                foreach (var diagnostic in tick.Diagnostics)
                {
                    if (diagnostic.Code == ErrorCodes.OW_UNITY_008)
                    {
                        failure = diagnostic;
                    }
                }
                Assert.That(
                    failure, Is.Not.Null,
                    "the restored wait must fail with OW-UNITY-008");
                Assert.That(
                    failure!.Hint, Does.Contain("abrupt evidence"),
                    "the import error journaled after the last token " +
                    "must still be attached");
            }
            finally
            {
                second?.Dispose();
                first?.Dispose();
                LogAssert.ignoreFailingMessages = ignore;
                ImportErrorCapture.Clear();
                if (Directory.Exists(storeRoot))
                {
                    try { Directory.Delete(storeRoot, recursive: true); }
                    catch (Exception) { /* a held file stays for the next run */ }
                }
            }
        }

        // The partitioned-budget half of the same rule — the bound
        // violation the follow-up review found next: letting an error-level
        // entry journal past the spent §F6 bound "counted toward nothing"
        // made a waiting import step's Console records unbounded. The
        // bound now stays absolute — every journaled entry spends a slot —
        // and while the §G4 wait owns the step its last
        // ConsoleLogCapture.ErrorBudgetReserve slots are held for
        // error-level entries: a warning flood stops at
        // MaxEntriesPerStep - ErrorBudgetReserve, so the import errors it
        // would have priced out still journal INSIDE the bound, each
        // flushed durable at append time, and the recovered wait's
        // OW-UNITY-008 attaches them after an abrupt session loss.
        [Test]
        public void Poll_ErrorsAfterWarningFlood_SurviveAbruptSessionLoss()
        {
            var storeRoot = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library",
                "OpWeaveAssetImportRestart",
                "t" + Guid.NewGuid().ToString("N"));
            var evidences = new[]
            {
                "Asset import failed for '" + BrokenPath + "' - flood evidence 0",
                "Asset import failed for '" + BrokenPath + "' - flood evidence 1",
                "Asset import failed for '" + BrokenPath + "' - flood evidence 2",
            };
            ImportErrorCapture.Clear();
            var ignore = LogAssert.ignoreFailingMessages;
            RunnerHostFixture? first = null;
            RunnerHostFixture? second = null;
            try
            {
                // Two checks in, the persisted StepWaiting carries
                // {nulls:2} and no captured errors — the resume token the
                // abrupt loss restores the waiting step from.
                first = new RunnerHostFixture(storeRoot);
                RegisterImport(first);
                var runId = AcceptImportWait(first);
                PollTicks(first, 2);
                Assert.IsTrue(
                    RunnerHostFixture.JournalHasAll(
                        storeRoot, runId,
                        "\"type\":\"step_waiting\"", "\"nulls\":2"),
                    "the last persisted token must precede the late import error");

                LogAssert.ignoreFailingMessages = true;
                // The warning flood: a non-error entry stops once the
                // unreserved share of the §F6 budget
                // (MaxEntriesPerStep - ErrorBudgetReserve) is spent, so
                // the reserved tail is still free for the import errors.
                for (var i = 0; i < ConsoleLogCapture.MaxEntriesPerStep; i++)
                {
                    Debug.LogWarning("flood-warning-" + i);
                }
                // The late import errors: Console-reported while the
                // waiting step is still the Run's latest attempt. Each
                // journals inside the bound and the waiting capture's
                // bound flush makes the Buffered record durable
                // immediately — NO FlushJournal and no lifecycle hook is
                // called, so the journal file must already carry them,
                // which is exactly the state an abrupt termination leaves.
                foreach (var evidence in evidences)
                {
                    Debug.LogError(evidence);
                }
                foreach (var evidence in evidences)
                {
                    Assert.IsTrue(
                        RunnerHostFixture.JournalHasAll(
                            storeRoot, runId,
                            "\"type\":\"log\"", "\"unity_console\"",
                            evidence),
                        "every import error must reach the journal file " +
                        "inside the bound without a lifecycle flush");
                }
                var consoleRecords = RunnerHostFixture.JournalCount(
                    storeRoot, runId, "\"unity_console\"");
                Assert.That(
                    consoleRecords,
                    Is.LessThanOrEqualTo(ConsoleLogCapture.MaxEntriesPerStep),
                    "no step may ever write past the §F6 Console bound");
                Assert.That(
                    consoleRecords,
                    Is.EqualTo(
                        ConsoleLogCapture.MaxEntriesPerStep -
                        ConsoleLogCapture.ErrorBudgetReserve +
                        evidences.Length),
                    "the warnings fill only the unreserved share and " +
                    "every error journals inside the bound");
                Assert.IsFalse(
                    RunnerHostFixture.JournalHas(
                        storeRoot, runId, "flood-warning-49"),
                    "a warning past the unreserved share must be dropped");
                ImportErrorCapture.Clear();
                first.Dispose();
                first = null;

                second = new RunnerHostFixture(storeRoot);
                RegisterImport(second);
                var recovered = second.Host.RecoverRuns();
                Assert.That(
                    recovered.Ok, Is.True,
                    () => DescribeDiagnostics(recovered.Diagnostics));
                second.Clock.Advance(TimeSpan.FromMilliseconds(150));
                var tick = second.Host.Tick();
                Assert.That(
                    tick.Ok, Is.True,
                    () => DescribeDiagnostics(tick.Diagnostics));
                Assert.That(
                    second.Host.Coordinator.StateOf(runId),
                    Is.EqualTo(RunState.Failed),
                    "the restored wait's third consecutive null ends it");

                Diagnostic? failure = null;
                foreach (var diagnostic in tick.Diagnostics)
                {
                    if (diagnostic.Code == ErrorCodes.OW_UNITY_008)
                    {
                        failure = diagnostic;
                    }
                }
                Assert.That(
                    failure, Is.Not.Null,
                    "the restored wait must fail with OW-UNITY-008");
                foreach (var evidence in evidences)
                {
                    Assert.That(
                        failure!.Hint, Does.Contain(evidence),
                        "every import error journaled inside the bound " +
                        "must still be attached");
                }
            }
            finally
            {
                second?.Dispose();
                first?.Dispose();
                LogAssert.ignoreFailingMessages = ignore;
                ImportErrorCapture.Clear();
                if (Directory.Exists(storeRoot))
                {
                    try { Directory.Delete(storeRoot, recursive: true); }
                    catch (Exception) { /* a held file stays for the next run */ }
                }
            }
        }

        // The ceiling half of the same fix: the error lane is reserved,
        // not unbounded — once the unreserved share is spent only the
        // first ErrorBudgetReserve error-level entries journal and every
        // further one drops, so the journal holds exactly
        // MaxEntriesPerStep Console records for the step however long the
        // error flood runs.
        [Test]
        public void Poll_ErrorsBeyondReservedLane_StayWithinConsoleBound()
        {
            var storeRoot = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library",
                "OpWeaveAssetImportRestart",
                "t" + Guid.NewGuid().ToString("N"));
            var flood = ConsoleLogCapture.MaxEntriesPerStep +
                ConsoleLogCapture.ErrorBudgetReserve;
            ImportErrorCapture.Clear();
            var ignore = LogAssert.ignoreFailingMessages;
            RunnerHostFixture? fixture = null;
            try
            {
                fixture = new RunnerHostFixture(storeRoot);
                RegisterImport(fixture);
                var runId = AcceptImportWait(fixture);
                PollTicks(fixture, 2);

                LogAssert.ignoreFailingMessages = true;
                for (var i = 0; i < ConsoleLogCapture.MaxEntriesPerStep; i++)
                {
                    Debug.LogWarning("flood-warning-" + i);
                }
                // The error flood: MaxEntriesPerStep + ErrorBudgetReserve
                // errors so even the reserved lane overflows — the bound
                // must hold at exactly 50 journaled Console records.
                for (var i = 0; i < flood; i++)
                {
                    Debug.LogError("flood-error-" + i);
                }
                Assert.IsTrue(
                    fixture.Host.FlushJournal().Ok,
                    "the flush must persist the pending buffered records");

                Assert.That(
                    RunnerHostFixture.JournalCount(
                        storeRoot, runId, "\"unity_console\""),
                    Is.EqualTo(ConsoleLogCapture.MaxEntriesPerStep),
                    "the §F6 bound is absolute: 40 warnings plus the " +
                    "first 10 errors, nothing more");
                for (var i = 0; i < ConsoleLogCapture.ErrorBudgetReserve; i++)
                {
                    Assert.IsTrue(
                        RunnerHostFixture.JournalHasAll(
                            storeRoot, runId,
                            "\"unity_console\"", "flood-error-" + i),
                        "the first reserved-lane errors must journal");
                }
                for (var i = ConsoleLogCapture.ErrorBudgetReserve; i < flood; i++)
                {
                    Assert.IsFalse(
                        RunnerHostFixture.JournalHas(
                            storeRoot, runId, "flood-error-" + i),
                        "an error past the bound must be dropped");
                }
                for (var i = ConsoleLogCapture.MaxEntriesPerStep -
                        ConsoleLogCapture.ErrorBudgetReserve;
                    i < ConsoleLogCapture.MaxEntriesPerStep;
                    i++)
                {
                    Assert.IsFalse(
                        RunnerHostFixture.JournalHas(
                            storeRoot, runId, "flood-warning-" + i),
                        "a warning past the unreserved share must be dropped");
                }
            }
            finally
            {
                fixture?.Dispose();
                LogAssert.ignoreFailingMessages = ignore;
                ImportErrorCapture.Clear();
                if (Directory.Exists(storeRoot))
                {
                    try { Directory.Delete(storeRoot, recursive: true); }
                    catch (Exception) { /* a held file stays for the next run */ }
                }
            }
        }

        // ── the live import ──────────────────────────────────────────────

        // local:T-W2.7-02:asset-import-wait pass condition, first half: a
        // valid file completes after the import — Execute queues it, the
        // §G4 condition sees !isUpdating and a loadable main asset, and the
        // step succeeds with the AssetRef output.
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator Import_ExistingFile_CompletesAfterTheImport()
        {
            WriteFile(TextPath, "opweave import probe\n");
            var op = new ImportAssetOperation();
            var context = Context(TextPath);

            var result = op.Execute(context);
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting), () => Describe(result));
            var token = result.ResumeToken!;

            var guard = 0;
            while (result.Kind == StepResultKind.Waiting && guard++ < 6000)
            {
                yield return null;
                context = Context(TextPath, resumeToken: token);
                result = op.Poll(context);
                if (result.Kind == StepResultKind.Waiting)
                {
                    token = result.ResumeToken!;
                }
            }

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var emitted = (JObject)context.Outputs[ImportAssetOperation.AssetPort].ToToken();
            Assert.That((string?)emitted["path"], Is.EqualTo(TextPath));
            Assert.That((string?)emitted["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(TextPath)));
            Assert.That(AssetDatabase.LoadMainAssetAtPath(TextPath), Is.Not.Null);
        }

        // local:T-W2.7-02:asset-import-wait pass condition, second half: an
        // unimportable file — a .asset file the YAML importer cannot parse —
        // is Failed with OW-UNITY-008 after three null loads while the
        // Editor is not updating.
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator Import_UnimportableFile_FailsWithOwUnity008()
        {
            WriteFile(BrokenPath, "%% this is not serialized yaml %%\n[[[");
            var previous = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                var op = new ImportAssetOperation();
                var context = Context(BrokenPath);

                var result = op.Execute(context);
                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting), () => Describe(result));
                var token = result.ResumeToken!;

                var guard = 0;
                while (result.Kind == StepResultKind.Waiting && guard++ < 6000)
                {
                    yield return null;
                    context = Context(BrokenPath, resumeToken: token);
                    result = op.Poll(context);
                    if (result.Kind == StepResultKind.Waiting)
                    {
                        token = result.ResumeToken!;
                    }
                }

                Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
                Assert.That(result.Error!.Code, Is.EqualTo(ErrorCodes.OW_UNITY_008));
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previous;
            }
        }

        // ── fixtures ─────────────────────────────────────────────────────

        // Registers the production unity.asset.import as a Standard
        // Operation over the scripted seams — the file exists, the queued
        // import reports nothing, the main asset never loads and the
        // Editor never updates — while the error channel stays the live
        // ImportErrorCapture the production condition drains, so the
        // journaled-vs-session read-back is exercised for real.
        private static void RegisterImport(RunnerHostFixture fixture)
        {
            var op = new ImportAssetOperation(
                _ => { }, _ => true, _ => null,
                new AssetImportedCondition(
                    () => false, _ => null,
                    () => ImportErrorCapture.RecentErrors));
            fixture.Operations.Add(new RegisteredOperation(
                op.Descriptor,
                OperationOrigin.Standard,
                "OpWeave.Operations",
                null,
                new string('a', 64),
                new string('b', 64))
            {
                Implementation = op,
            });
        }

        // Accepts the one-step import Plan on the fixture and drives the
        // Execute tick: the step is left Waiting with {nulls:0} persisted.
        private static string AcceptImportWait(RunnerHostFixture fixture)
        {
            var plan = RunnerHostFixture.Plan(
                "example.assetimport.restart",
                new[]
                {
                    RunnerHostFixture.Step(
                        0, "import", ImportAssetOperation.OperationId, 'a',
                        inputs: new Dictionary<string, InputSource>
                        {
                            [ImportAssetOperation.PathPort] =
                                new Constant(Json("\"" + BrokenPath + "\"")),
                        },
                        outputTypes: OutPorts,
                        idempotency: Idempotency.Idempotent),
                });
            var doc = RunnerHostFixture.Doc(
                "example.assetimport.restart",
                new[]
                {
                    new NodeData
                    {
                        Id = "import",
                        Operation = ImportAssetOperation.OperationId,
                        Version = 1,
                    },
                },
                Array.Empty<ControlEdge>());
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.That(
                accepted.Ok, Is.True,
                () => DescribeDiagnostics(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            var tick = fixture.Host.Tick();
            Assert.That(
                tick.Ok, Is.True, () => DescribeDiagnostics(tick.Diagnostics));
            Assert.That(
                fixture.Host.Coordinator.StateOf(runId),
                Is.EqualTo(RunState.Running));
            return runId;
        }

        // Advances the fake clock past each next poll and Ticks once per
        // poll — every Waiting return journals a fresh StepWaiting.
        private static void PollTicks(RunnerHostFixture fixture, int polls)
        {
            for (var i = 0; i < polls; i++)
            {
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(150));
                var tick = fixture.Host.Tick();
                Assert.That(
                    tick.Ok, Is.True,
                    () => DescribeDiagnostics(tick.Diagnostics));
            }
        }

        private static string DescribeDiagnostics(
            IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return parts.Count == 0
                ? "<no diagnostics>"
                : string.Join("; ", parts);
        }

        // A condition whose verdicts the test scripts — the Poll wiring
        // needs no real condition.
        private sealed class ScriptedCondition : IWaitCondition
        {
            private readonly WaitStatus _status;
            internal ScriptedCondition(WaitStatus status) => _status = status;
            public WaitStatus Check(JsonValue token) => _status;
        }

        private sealed class NeverFinishes : IWaitCondition
        {
            internal static readonly NeverFinishes Instance = new NeverFinishes();
            public WaitStatus Check(JsonValue token) => WaitStatus.Pending(token);
        }

        private static void EnsureFolder(string folderPath)
        {
            var segments = folderPath.Split('/');
            var parent = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var child = parent + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(child))
                {
                    Assert.That(
                        AssetDatabase.CreateFolder(parent, segments[i]),
                        Is.Not.Empty,
                        "the probe folder must be created: " + child);
                }
                parent = child;
            }
        }

        private static void WriteFile(string assetPath, string text)
        {
            var physical = Path.Combine(
                Application.dataPath,
                assetPath.Substring("Assets/".Length)
                    .Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(physical, text);
        }

        private static UnityEngine.Object CreateTextAsset(string assetPath)
        {
            WriteFile(assetPath, "opweave import probe\n");
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            Assert.That(asset, Is.Not.Null, "the probe asset must import: " + assetPath);
            return asset!;
        }

        private static JsonValue Token()
        {
            var parsed = JsonValue.Parse(
                "{\"paths\":[\"" + TextPath + "\"],\"nulls\":0,\"errors\":[]}");
            Assert.That(parsed.Ok, Is.True, "the fixture token must parse");
            return parsed.Value!;
        }

        private static UnityStepContext Context(string path, JsonValue? resumeToken = null) =>
            new UnityStepContext(
                new Dictionary<string, InputValue>
                {
                    [ImportAssetOperation.PathPort] = InputValue.Present(
                        Json("\"" + path.Replace("\\", "\\\\") + "\"")),
                },
                Ports, OutPorts,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "import"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                resumeToken,
                null);

        private static void AssertFailedWith(StepResult result, string code, string port)
        {
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed), () => Describe(result));
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo(code), () => Describe(result));
            Assert.That(result.Error!.Port, Is.EqualTo(port));
        }

        private static string Describe(StepResult result) =>
            result.Kind +
            (result.Error is null
                ? string.Empty
                : " " + result.Error.Code + " " + result.Error.Message);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }
    }
}
