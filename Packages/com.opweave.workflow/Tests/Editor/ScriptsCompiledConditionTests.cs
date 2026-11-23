#nullable enable

// T-W4.1-01 [Edit] tests: the SPEC §G4 ScriptsCompiled(token) row — the
// wait's whole state restores from the resume token alone, and it judges
// Done only after observing compilation completion (a reload count bump,
// a pipeline completion event, or a compile the check itself saw running)
// on an idle Editor without the failure flag; the flag yields OW-RUN-009
// with the captured error lines attached.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// SPEC §G4: <c>ScriptsCompiled(token)</c> — completion on observed
    /// compilation completion or a ReloadCount increase with the Editor
    /// idle and unfailed, OW-RUN-009 with the error list on failure.
    /// </summary>
    internal sealed class ScriptsCompiledConditionTests
    {
        private static readonly IReadOnlyList<string> NoErrors =
            Array.Empty<string>();

        [SetUp]
        public void SetUp() => CompileErrorCapture.Clear();

        [TearDown]
        public void TearDown() => CompileErrorCapture.Clear();

        private static ScriptsCompiledCondition Condition(
            Func<bool>? isCompiling = null,
            Func<bool>? isUpdating = null,
            Func<bool>? scriptCompilationFailed = null,
            Func<int>? reloadCount = null,
            Func<int>? compileFinishedCount = null,
            Func<IReadOnlyList<string>>? recentCompileErrors = null) =>
            new ScriptsCompiledCondition(
                isCompiling ?? (static () => false),
                isUpdating ?? (static () => false),
                scriptCompilationFailed ?? (static () => false),
                reloadCount ?? (static () => 7),
                compileFinishedCount ?? (static () => 3),
                recentCompileErrors ?? (static () => NoErrors));

        private static JsonValue Token(
            int reloadCount = 7, int compileCount = 3,
            bool sawCompiling = false, params string[] errors) =>
            JsonValue.From(new JObject
            {
                ["reloadCount"] = reloadCount,
                ["compileCount"] = compileCount,
                ["sawCompiling"] = sawCompiling,
                ["errors"] = new JArray(errors),
            });

        [Test]
        public void Check_NothingObserved_StaysPending()
        {
            var verdict = Condition().Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_ReloadCountIncreased_ReturnsDone()
        {
            // The §G4 cross-reload signal: the domain the request ran in is
            // gone, so the live counter moved past the token's snapshot.
            var verdict = Condition(reloadCount: () => 8).Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void Check_CompileFinishedIncreased_ReturnsDone()
        {
            // The L-05 no-reload path: a compilationFinished — or the
            // no-change assemblyCompilationNotRequired — counts as
            // observed completion even with the domain standing.
            var verdict = Condition(compileFinishedCount: () => 4).Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void Check_SawCompilingThenIdle_ReturnsDone()
        {
            // A compile that started and ended between two checks still
            // counts: the first check records sawCompiling in the token.
            var pending = Condition(isCompiling: () => true).Check(Token());
            Assert.That(pending.Kind, Is.EqualTo(WaitStatusKind.Pending));
            var verdict = Condition().Check(pending.ResumeToken!);
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void Check_Compiling_ReturnsPendingAndRecordsSawCompiling()
        {
            var verdict = Condition(isCompiling: () => true).Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
            var token = (JObject)verdict.ResumeToken!.ToToken();
            Assert.That((bool)token["sawCompiling"]!, Is.True);
        }

        [Test]
        public void Check_Updating_ReturnsPending()
        {
            var verdict = Condition(isUpdating: () => true).Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_FailedFlagWhileCompiling_StaysPending()
        {
            // scriptCompilationFailed cannot settle under the compile it
            // reports — judgement waits for the idle check.
            var verdict = Condition(
                    isCompiling: () => true,
                    scriptCompilationFailed: () => true)
                .Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_FailedFlagWhileUpdating_StaysPending()
        {
            var verdict = Condition(
                    isUpdating: () => true,
                    scriptCompilationFailed: () => true)
                .Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_ScriptCompilationFailed_ReturnsOwRun009WithErrors()
        {
            var verdict = Condition(
                    scriptCompilationFailed: () => true,
                    recentCompileErrors: () => new[]
                    {
                        "Assets/Broken.cs(3,7): error CS1002: ; expected",
                    })
                .Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Failed));
            var error = verdict.Error!;
            Assert.That(error.Code, Is.EqualTo("OW-RUN-009"));
            Assert.That(error.Hint, Does.Contain("CS1002"));
            Assert.That(error.Hint, Does.Contain("Broken.cs"));
        }

        [Test]
        public void Check_FailedWithoutCapturedErrors_StillFails()
        {
            var verdict = Condition(scriptCompilationFailed: () => true)
                .Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Failed));
            Assert.That(verdict.Error!.Code, Is.EqualTo("OW-RUN-009"));
            Assert.That(verdict.Error!.Hint, Does.Contain("Editor log"));
        }

        [Test]
        public void Check_ErrorsFoldedIntoPendingToken()
        {
            // An error reported mid-wait is folded into the next Pending
            // token so the reload never takes it.
            var verdict = Condition(
                    isCompiling: () => true,
                    recentCompileErrors: () => new[]
                    {
                        "Assets/A.cs(1,1): error CS0001: boom",
                    })
                .Check(Token());
            var token = (JObject)verdict.ResumeToken!.ToToken();
            var errors = (JArray)token["errors"]!;
            Assert.That(errors.Count, Is.EqualTo(1));
            Assert.That((string)errors[0]!, Does.Contain("CS0001"));
        }

        [Test]
        public void Check_ErrorsFromTokenAreKeptAndMerged()
        {
            var verdict = Condition(
                    scriptCompilationFailed: () => true,
                    recentCompileErrors: () => new[]
                    {
                        "Assets/B.cs(2,2): error CS0002: second",
                    })
                .Check(Token(errors: "Assets/A.cs(1,1): error CS0001: first"));
            Assert.That(verdict.Error!.Code, Is.EqualTo("OW-RUN-009"));
            Assert.That(verdict.Error!.Hint, Does.Contain("CS0001"));
            Assert.That(verdict.Error!.Hint, Does.Contain("CS0002"));
        }

        [Test]
        public void Check_ErrorsBoundedAtMaxCollected()
        {
            var recent = new List<string>();
            for (var i = 0; i < 20; i++)
            {
                recent.Add($"Assets/E{i}.cs(1,1): error CS{i:D4}: boom");
            }
            var verdict = Condition(
                    isCompiling: () => true,
                    recentCompileErrors: () => recent)
                .Check(Token());
            var errors = (JArray)((JObject)verdict.ResumeToken!.ToToken())["errors"]!;
            Assert.That(errors.Count,
                Is.EqualTo(ScriptsCompiledCondition.MaxCollectedErrors));
        }

        [Test]
        public void Check_TokenIsTheWholeState_FreshConditionContinues()
        {
            // Two condition instances handed the same token judge it
            // identically — the §G4 "条件は resumeToken だけから復元できる" rule.
            var token = Token();
            var first = Condition(reloadCount: () => 9).Check(token);
            var second = Condition(reloadCount: () => 9).Check(token);
            Assert.That(first.Kind, Is.EqualTo(second.Kind));
            Assert.That(first.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void Check_NullToken_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => Condition().Check(null!));
        }

        [Test]
        public void Check_TokenWithoutCounters_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => Condition().Check(
                    JsonValue.From(new JObject { ["errors"] = new JArray() })));
        }

        [Test]
        public void Check_TokenWithNegativeCount_Throws()
        {
            Assert.Throws<ArgumentException>(() => Condition().Check(
                JsonValue.From(new JObject
                {
                    ["reloadCount"] = -1,
                    ["compileCount"] = 0,
                })));
        }

        [Test]
        public void Check_NonObjectToken_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => Condition().Check(JsonValue.From(new JArray(1, 2))));
        }

        [Test]
        public void InitialToken_SnapshotsLiveCounters()
        {
            var token = ScriptsCompiledCondition.InitialToken(11, 4);
            var obj = (JObject)token.ToToken();
            Assert.That((int)obj["reloadCount"]!, Is.EqualTo(11));
            Assert.That((int)obj["compileCount"]!, Is.EqualTo(4));
            Assert.That((bool)obj["sawCompiling"]!, Is.False);
            Assert.That(((JArray)obj["errors"]!).Count, Is.EqualTo(0));
        }

        [Test]
        public void InitialToken_ResumedAfterBothCountersMove_IsDone()
        {
            // The request-time snapshot: either counter moving past it is
            // observed completion.
            var token = ScriptsCompiledCondition.InitialToken(5, 9);
            Assert.That(
                Condition(compileFinishedCount: () => 10).Check(token).Kind,
                Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void InitialToken_ArmsTheCaptureWindow()
        {
            // The public §G4 lifecycle the custom Operation follows: the
            // token factory opens the step's compile-error capture before
            // the request — the T-W4.1-01 Codex F1 fix — so a compile that
            // fails before the first check still reaches the capture.
            ScriptsCompiledCondition.InitialToken();
            Assert.That(CompileErrorCapture.WaitWindowOpen, Is.True);
        }

        [Test]
        public void Check_Done_ClosesTheCaptureWindow()
        {
            var token = ScriptsCompiledCondition.InitialToken();
            var verdict = Condition(
                    compileFinishedCount: () => int.MaxValue)
                .Check(token);
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
            Assert.That(CompileErrorCapture.WaitWindowOpen, Is.False);
        }

        [Test]
        public void Check_Failed_ClosesTheCaptureWindow()
        {
            var token = ScriptsCompiledCondition.InitialToken();
            var verdict = Condition(scriptCompilationFailed: () => true)
                .Check(token);
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Failed));
            Assert.That(verdict.Error!.Code, Is.EqualTo("OW-RUN-009"));
            Assert.That(CompileErrorCapture.WaitWindowOpen, Is.False);
        }

        [Test]
        public void EndWait_ClosesTheCaptureWindowWithoutATerminalCheck()
        {
            // The public completion hook for the paths that end the wait
            // without a terminal check — a cancelled Run's Cancel, say.
            ScriptsCompiledCondition.InitialToken();
            ScriptsCompiledCondition.EndWait();
            Assert.That(CompileErrorCapture.WaitWindowOpen, Is.False);
        }

        [Test]
        public void ReloadCounter_CountDomain_BumpsSessionState()
        {
            var before = SessionState.GetInt(DomainReloadCounter.SessionKey, 0);
            try
            {
                DomainReloadCounter.CountDomain();
                Assert.That(
                    SessionState.GetInt(DomainReloadCounter.SessionKey, 0),
                    Is.EqualTo(before + 1));
                Assert.That(DomainReloadCounter.Current, Is.EqualTo(before + 1));
            }
            finally
            {
                SessionState.SetInt(DomainReloadCounter.SessionKey, before);
            }
        }
    }
}
