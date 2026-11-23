#nullable enable

// T-W4.2-02 [Edit] tests: the unity.script.compile halves of
// local:T-W4.2-02:script-create-compile (SPEC §G8 item 3, §I.4, §G4).
//
// Coverage at the Operation level (the real compile drive lives in
// ScriptCompileReloadTests):
//  - Execute arms both §G4 capture windows, requests the compilation and
//    returns Waiting with the ScriptsCompiled resume token — every state
//    the wait needs lives in the token (SPEC §C2 item 4).
//  - Poll continues a Pending wait with the folded warnings injected back
//    into the token (they survive a reload), a Done verdict lands the
//    §I.4 { errors, warnings } result, and a Failed verdict is the
//    condition's OW-RUN-009 with the captured error lines.
//  - Cancel drops both capture windows — the §G4 lifecycle's non-terminal
//    close path.
//  - CompileWarningCapture itself: window gating, dedupe/bound,
//    SessionState persistence across a simulated domain reload.

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The Waitable lifecycle of <c>unity.script.compile</c> over its
    /// <see cref="EditorStateProbe"/>-style seams, plus the warning
    /// capture's own contract.
    /// </summary>
    internal sealed class ScriptCompileOperationTests
    {
        private static readonly IReadOnlyDictionary<string, string> OutputTypes =
            new Dictionary<string, string> { ["result"] = "core.Json" };

        private static readonly CompilerMessage Warning = new CompilerMessage
        {
            type = CompilerMessageType.Warning,
            file = "Assets/Scripts/Noisy.cs",
            line = 2,
            column = 4,
            message = "warning CS0168: variable never used",
        };

        private static readonly CompilerMessage Error = new CompilerMessage
        {
            type = CompilerMessageType.Error,
            file = "Assets/Scripts/Broken.cs",
            line = 5,
            column = 9,
            message = "error CS1002: ; expected",
        };

        [SetUp]
        public void SetUp()
        {
            CompileErrorCapture.Clear();
            CompileWarningCapture.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            CompileErrorCapture.Clear();
            CompileWarningCapture.Clear();
        }

        // A wait condition the case scripts — the EditorStateProbe pattern
        // the Operation's internal constructor exists for.
        private sealed class StubCondition : IWaitCondition
        {
            private readonly Queue<WaitStatus> _statuses;
            internal readonly List<JsonValue> Tokens = new List<JsonValue>();

            internal StubCondition(params WaitStatus[] statuses)
            {
                _statuses = new Queue<WaitStatus>(statuses);
            }

            public WaitStatus Check(JsonValue token)
            {
                Tokens.Add(token);
                return _statuses.Count == 0
                    ? WaitStatus.Pending(token)
                    : _statuses.Dequeue();
            }
        }

        private static CompileScriptsOperation Op(
            Action requestCompilation, IWaitCondition condition) =>
            new CompileScriptsOperation(requestCompilation, condition);

        private static UnityStepContext Context(JsonValue? resumeToken = null) =>
            new UnityStepContext(
                new Dictionary<string, InputValue>(),
                new Dictionary<string, string>(),
                OutputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "compile"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                resumeToken);

        // SPEC §I.4: Execute requests the script compilation and waits —
        // the resume token is the §G4 ScriptsCompiled token (counter
        // snapshots + errors), and both capture windows are armed first so
        // a compile answering before the first Poll still attributes its
        // messages to this step.
        [Test]
        public void Execute_RequestsCompilation_ReturnsWaitingWithArmedCapture()
        {
            var requests = 0;
            var op = Op(() => requests++, new StubCondition());

            var result = op.Execute(Context());

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting));
            Assert.That(requests, Is.EqualTo(1),
                "RequestScriptCompilation is issued exactly once");
            Assert.That(result.WaitingFor,
                Is.EqualTo(CompileScriptsOperation.WaitingForLabel));
            var token = (JObject)result.ResumeToken!.ToToken();
            Assert.That(token["reloadCount"], Is.Not.Null);
            Assert.That(token["compileCount"], Is.Not.Null);
            Assert.That(CompileErrorCapture.WaitWindowOpen, Is.True,
                "the §G4 error window is armed");
            Assert.That(CompileWarningCapture.WaitWindowOpen, Is.True,
                "the warning window is armed");
        }

        // A Pending check continues the wait; warnings the pipeline
        // reported are folded into the next token — the only state that
        // survives a reload — deduped against what the token already holds.
        [Test]
        public void Poll_Pending_FoldsWarningsIntoResumeToken()
        {
            var nextToken = JsonValue.Parse(
                "{\"reloadCount\":1,\"compileCount\":1,\"sawCompiling\":false,\"errors\":[]}")
                .Value!;
            var stub = new StubCondition(WaitStatus.Pending(nextToken));
            var op = Op(() => { }, stub);
            var waiting = op.Execute(Context());
            CompileWarningCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Warning });

            var polled = op.Poll(Context(waiting.ResumeToken));

            Assert.That(polled.Kind, Is.EqualTo(StepResultKind.Waiting));
            var folded = (JObject)polled.ResumeToken!.ToToken();
            var warnings = (JArray?)folded["warnings"];
            Assert.That(warnings, Is.Not.Null.And.Count.EqualTo(1));
            Assert.That((string?)warnings![0], Does.Contain("CS0168"));
            Assert.That((string?)warnings[0], Does.Contain("Noisy.cs"));

            // A second Pending keeps what the token already folded —
            // simulated post-reload: the mirror died, the token carries on.
            CompileWarningCapture.SimulateDomainReload();
            var stub2 = new StubCondition(WaitStatus.Pending(nextToken));
            var polled2 = Op(() => { }, stub2).Poll(Context(polled.ResumeToken));
            var folded2 = (JObject)polled2.ResumeToken!.ToToken();
            Assert.That(((JArray?)folded2["warnings"]), Has.Count.EqualTo(1),
                "token-folded warnings survive the reload the mirror lost");
        }

        // Done lands the §I.4 result: errors the token folded (empty for a
        // passing compile) and the captured warning lines.
        [Test]
        public void Poll_Done_OutputsResultJson_AndClosesCapture()
        {
            var stub = new StubCondition(WaitStatus.Done());
            var op = Op(() => { }, stub);
            var waiting = op.Execute(Context());
            CompileWarningCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Warning });
            var context = Context(waiting.ResumeToken);

            var result = op.Poll(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var payload = (JObject)context.Outputs["result"].ToToken();
            Assert.That(((JArray?)payload["errors"]), Is.Not.Null.And.Count.EqualTo(0));
            var warnings = (JArray?)payload["warnings"];
            Assert.That(warnings, Is.Not.Null.And.Count.EqualTo(1));
            Assert.That((string?)warnings![0], Does.Contain("warning CS0168"));
            Assert.That(CompileWarningCapture.WaitWindowOpen, Is.False,
                "a terminal verdict closes the warning window too");
        }

        // A failed compile is the condition's OW-RUN-009 with the captured
        // error line in the hint — the same diagnostic the Run surfaces.
        [Test]
        public void Poll_Failed_FailsWithOwRun009_AndClosesCapture()
        {
            var failure = ErrorCodes.Create(
                ErrorCodes.OW_RUN_009,
                "The script compilation failed.",
                hint: "Compile errors: Assets/Scripts/Broken.cs(5,9): error CS1002: ; expected");
            var stub = new StubCondition(WaitStatus.Failed(failure));
            var op = Op(() => { }, stub);
            var waiting = op.Execute(Context());

            var result = op.Poll(Context(waiting.ResumeToken));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error!.Code, Is.EqualTo(ErrorCodes.OW_RUN_009));
            Assert.That(result.Error!.Hint, Does.Contain("error CS1002"));
            Assert.That(CompileWarningCapture.WaitWindowOpen, Is.False);
        }

        // Cancel is the §G4 lifecycle's non-terminal close: both capture
        // windows drop so a later step never reads this wait's entries.
        [Test]
        public void Cancel_ClosesBothCaptureWindows()
        {
            var op = Op(() => { }, new StubCondition());
            var waiting = op.Execute(Context());
            Assert.That(CompileWarningCapture.WaitWindowOpen, Is.True);

            op.Cancel(Context(waiting.ResumeToken));

            Assert.That(CompileErrorCapture.WaitWindowOpen, Is.False);
            Assert.That(CompileWarningCapture.WaitWindowOpen, Is.False);
        }

        // ── CompileWarningCapture itself ─────────────────────────────────

        [Test]
        public void WarningCapture_CapturesWhileWaiting_NeverErrorsOrInfo()
        {
            CompileWarningCapture.BeginStep();
            CompileWarningCapture.OnAssemblyCompilationFinished(
                "probe.dll",
                new[]
                {
                    Warning,
                    Error,
                    new CompilerMessage
                    {
                        type = CompilerMessageType.Info,
                        file = "", line = 0, column = 0, message = "info",
                    },
                });

            var recent = CompileWarningCapture.RecentWarnings;
            Assert.That(recent, Has.Count.EqualTo(1));
            Assert.That(recent[0], Does.Contain("Noisy.cs"));
            Assert.That(recent[0], Does.Contain("CS0168"));
        }

        [Test]
        public void WarningCapture_WindowClosed_CapturesNothing()
        {
            CompileWarningCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Warning });
            Assert.That(CompileWarningCapture.RecentWarnings, Is.Empty);
        }

        [Test]
        public void WarningCapture_Mirror_SurvivesSimulatedDomainReload()
        {
            CompileWarningCapture.BeginStep();
            CompileWarningCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Warning });

            CompileWarningCapture.SimulateDomainReload();

            Assert.That(CompileWarningCapture.RecentWarnings, Has.Count.EqualTo(1));
            Assert.That(CompileWarningCapture.WaitWindowOpen, Is.True,
                "the armed window survives the reload like the error window's");
        }

        [Test]
        public void WarningCapture_BeginStep_DropsPriorCapture()
        {
            CompileWarningCapture.BeginStep();
            CompileWarningCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Warning });
            CompileWarningCapture.EndStep();
            CompileWarningCapture.BeginStep();
            Assert.That(CompileWarningCapture.RecentWarnings, Is.Empty);
        }

        private static string Describe(StepResult result) =>
            result.Kind +
            (result.Error is null
                ? string.Empty
                : " " + result.Error.Code + " " + result.Error.Message);
    }
}
