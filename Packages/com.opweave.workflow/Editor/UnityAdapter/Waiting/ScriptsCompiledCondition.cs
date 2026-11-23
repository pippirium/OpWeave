#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using UnityEditor;
using UnityEditor.Compilation;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The <c>ScriptsCompiled(token)</c> row of SPEC §G4: the wait a
    /// <c>unity.package.*</c>/<c>unity.code.*</c> step (or a custom
    /// Operation requesting a recompile) performs —
    /// <see cref="WaitStatusKind.Done"/> once compilation completion has
    /// been observed after the request — a
    /// <see cref="CompilationPipeline.compilationFinished"/>,
    /// an <see cref="CompilationPipeline.assemblyCompilationNotRequired"/>,
    /// or a <see cref="DomainReloadCounter"/> increase — and the Editor is
    /// neither compiling nor updating and
    /// <see cref="EditorUtility.scriptCompilationFailed"/> is false;
    /// <see cref="WaitStatusKind.Failed"/> with OW-RUN-009 when the flag
    /// says the compile failed, carrying the step's captured compile
    /// errors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The resume token is the whole wait state</b> (SPEC §G4 "条件は
    /// resumeToken だけから復元できる"). It is a JSON object with four
    /// members — <c>reloadCount</c> (the §G4 SessionState reload counter
    /// snapshot at request time), <c>compileCount</c> (the observed
    /// completion counter snapshot), <c>sawCompiling</c> (the check once
    /// saw the Editor compiling, which is itself observable completion of
    /// that compile once it is no longer running) and <c>errors</c> (the
    /// compile errors this step already captured). A condition created in
    /// a fresh domain and handed the token continues exactly where the
    /// lost one stopped.
    /// </para>
    /// <para>
    /// <b>Observed completion, not "a reload happened"</b> (SPEC §T.4
    /// L-05). <c>RequestScriptCompilation</c> with no source changes may
    /// compile nothing and leave the domain standing, so an increased
    /// <see cref="DomainReloadCounter.Current"/> is only <i>one</i> of the
    /// completion signals; a bump of the completion counter
    /// (<see cref="CompileErrorCapture.FinishedCount"/> — which
    /// <c>assemblyCompilationNotRequired</c> also raises) or the saw-
    /// compiling edge carry the no-reload paths.
    /// </para>
    /// <para>
    /// <b>Judgement order.</b> While the Editor is compiling or updating
    /// nothing is judged — <c>scriptCompilationFailed</c> can only settle
    /// once the compile stops being the Editor's in-flight work — so the
    /// flag is read only on idle checks, after which the captured errors
    /// are attached to the OW-RUN-009 diagnostic (SPEC §G4 "エラー一覧を
    /// 添付"). The compile errors themselves reach the token through
    /// <see cref="CompileErrorCapture"/>: the pipeline's per-assembly
    /// event writes them to SessionState and to the step's journal in the
    /// callback, and each check folds the capture into the token, so an
    /// error reported between checks still reaches the failure verdict
    /// even when a domain reload came first.
    /// </para>
    /// <para>
    /// A token the step itself never wrote — no <c>reloadCount</c> or
    /// <c>compileCount</c> count, a non-string error entry — is malformed
    /// input, which is a caller bug, so <see cref="Check"/> refuses it
    /// with <see cref="ArgumentException"/> rather than misjudging it.
    /// </para>
    /// <para>
    /// <b>The public lifecycle (§G4 "カスタム Operation も利用できる").</b>
    /// <see cref="InitialToken"/> builds the request-time token <i>and</i>
    /// opens the step's compile-error capture window — the Operation calls
    /// it before requesting the compilation, so a compile that fails
    /// before the first <see cref="Check"/> still reaches SessionState and
    /// the journal; a terminal <see cref="Check"/> verdict
    /// (<see cref="WaitStatusKind.Done"/> or
    /// <see cref="WaitStatusKind.Failed"/>) closes the window again —
    /// the same completion path the import wait's
    /// <c>EndStep</c> rides — and <see cref="EndWait"/> covers the paths
    /// that end the wait without a terminal check, like a cancelled Run.
    /// The whole lifecycle runs through public members so a custom
    /// Operation waits exactly the way <c>unity.script.compile</c> does.
    /// </para>
    /// </remarks>
    public sealed class ScriptsCompiledCondition : IWaitCondition
    {
        /// <summary>The bound on the error lines one token accumulates.</summary>
        internal const int MaxCollectedErrors = 8;

        private const string ReloadCountField = "reloadCount";
        private const string CompileCountField = "compileCount";
        private const string SawCompilingField = "sawCompiling";
        private const string ErrorsField = "errors";

        private readonly Func<bool> _isCompiling;
        private readonly Func<bool> _isUpdating;
        private readonly Func<bool> _scriptCompilationFailed;
        private readonly Func<int> _reloadCount;
        private readonly Func<int> _compileFinishedCount;
        private readonly Func<IReadOnlyList<string>> _recentCompileErrors;

        /// <summary>Creates the condition over the live Editor state.</summary>
        public ScriptsCompiledCondition()
            : this(
                static () => EditorApplication.isCompiling,
                static () => EditorApplication.isUpdating,
                static () => EditorUtility.scriptCompilationFailed,
                static () => DomainReloadCounter.Current,
                static () => CompileErrorCapture.FinishedCount,
                static () => CompileErrorCapture.RecentErrors)
        {
        }

        /// <summary>
        /// The test seam over the live reads the check performs — the
        /// <see cref="EditorStateProbe"/> pattern — so the completion
        /// signals, the failure flag and the error attachment are provable
        /// without driving a real compile.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any reader is null.</exception>
        internal ScriptsCompiledCondition(
            Func<bool> isCompiling,
            Func<bool> isUpdating,
            Func<bool> scriptCompilationFailed,
            Func<int> reloadCount,
            Func<int> compileFinishedCount,
            Func<IReadOnlyList<string>> recentCompileErrors)
        {
            _isCompiling =
                isCompiling ?? throw new ArgumentNullException(nameof(isCompiling));
            _isUpdating =
                isUpdating ?? throw new ArgumentNullException(nameof(isUpdating));
            _scriptCompilationFailed =
                scriptCompilationFailed ??
                throw new ArgumentNullException(nameof(scriptCompilationFailed));
            _reloadCount =
                reloadCount ?? throw new ArgumentNullException(nameof(reloadCount));
            _compileFinishedCount =
                compileFinishedCount ??
                throw new ArgumentNullException(nameof(compileFinishedCount));
            _recentCompileErrors =
                recentCompileErrors ??
                throw new ArgumentNullException(nameof(recentCompileErrors));
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="token"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="token"/> is not a ScriptsCompiled token — no
        /// <c>reloadCount</c>/<c>compileCount</c> count of zero or more.
        /// </exception>
        public WaitStatus Check(JsonValue token)
        {
            if (token is null)
            {
                throw new ArgumentNullException(nameof(token));
            }
            var state = TokenState.Read(token);
            // A wait continued from its persisted token still owns the §G4
            // capture window: the BeginStep that armed it ran in the domain
            // (or Editor session) the reload/restart dropped, so the open
            // window is re-armed here — a compile error the resumed wait
            // still reports keeps the same immediate journal flush.
            CompileErrorCapture.ResumeWait();
            var errors = CollectErrors(state);

            // While the Editor is compiling or updating nothing is judged:
            // the flag cannot settle under the compile it reports, so the
            // check only records that it saw the Editor compiling — which
            // is itself the completion signal once the Editor is idle.
            var compiling = _isCompiling();
            var sawCompiling = state.SawCompiling || compiling;
            if (compiling || _isUpdating())
            {
                return WaitStatus.Pending(WriteToken(
                    state.ReloadCount, state.CompileCount, sawCompiling, errors));
            }

            if (_scriptCompilationFailed())
            {
                // The wait is over: the close the import wait's Operation
                // runs on the terminal verdict, folded into the check
                // itself so a custom Operation — which cannot call the
                // capture's EndStep — shares the same completion path.
                CompileErrorCapture.EndStep();
                return WaitStatus.Failed(CompileFailed(errors));
            }

            if (_reloadCount() > state.ReloadCount ||
                _compileFinishedCount() > state.CompileCount ||
                sawCompiling)
            {
                CompileErrorCapture.EndStep();
                return WaitStatus.Done();
            }
            return WaitStatus.Pending(WriteToken(
                state.ReloadCount, state.CompileCount, sawCompiling, errors));
        }

        /// <summary>
        /// The token a Waitable Operation stores in its first
        /// <see cref="StepResult.Waiting"/>: the counters snapshotted at
        /// request time — a compile already queued cannot have finished —
        /// with no observed state and no captured errors yet.
        /// </summary>
        /// <remarks>
        /// Building the token also opens the step's §G4 compile-error
        /// capture window — the <see cref="CompileErrorCapture.BeginStep"/>
        /// boundary that attributes the pipeline's error lines to this
        /// wait — so the Operation calls it <b>before</b> requesting the
        /// compilation the token waits on: a compile that fails before the
        /// first <see cref="Check"/> still writes its errors to
        /// SessionState and the journal in the callback. This is the same
        /// call a custom Operation makes (SPEC §G4 "カスタム Operation
        /// も利用できる").
        /// </remarks>
        public static JsonValue InitialToken()
        {
            CompileErrorCapture.BeginStep();
            return WriteToken(
                DomainReloadCounter.Current, CompileErrorCapture.FinishedCount,
                sawCompiling: false, Array.Empty<string>());
        }

        /// <summary>
        /// The step-end half of the §G4 compile-wait lifecycle the
        /// <see cref="InitialToken"/> arm begins: drops the wait's capture
        /// window for the paths that end the wait without a terminal
        /// <see cref="Check"/> — a cancelled Run's
        /// <see cref="IOperation.Cancel"/>, say. A
        /// <see cref="WaitStatusKind.Done"/> or
        /// <see cref="WaitStatusKind.Failed"/> verdict already closed it;
        /// calling again is a no-op.
        /// </summary>
        public static void EndWait() => CompileErrorCapture.EndStep();

        /// <summary>
        /// The initial token over explicit counter snapshots — the test
        /// seam that lets a check run against a token written in another
        /// (simulated) domain. Unlike <see cref="InitialToken()"/> it
        /// touches no live state: no capture window is armed.
        /// </summary>
        internal static JsonValue InitialToken(int reloadCount, int compileCount) =>
            WriteToken(
                reloadCount, compileCount,
                sawCompiling: false, Array.Empty<string>());

        // The drain of the step's compile errors into the token: the
        // capture's recent entries that are not already there join the
        // list, up to the bound. Errors never leave the token once folded
        // in — the merge is monotone, which is what keeps them across a
        // reload.
        private List<string> CollectErrors(TokenState state)
        {
            var errors = new List<string>(state.Errors);
            var recent = _recentCompileErrors();
            for (var i = 0;
                i < recent.Count && errors.Count < MaxCollectedErrors;
                i++)
            {
                if (!errors.Contains(recent[i]))
                {
                    errors.Add(recent[i]);
                }
            }
            return errors;
        }

        private static JsonValue WriteToken(
            int reloadCount, int compileCount, bool sawCompiling,
            IReadOnlyList<string> errors) =>
            JsonValue.From(new JObject
            {
                [ReloadCountField] = reloadCount,
                [CompileCountField] = compileCount,
                [SawCompilingField] = sawCompiling,
                [ErrorsField] = new JArray(errors),
            });

        // The OW-RUN-009 verdict with the step's compile errors attached
        // (SPEC §G4 "エラー一覧を添付"). The message stays the fixed
        // English sentence; the captured error lines go to the hint, which
        // is the Diagnostic member meant for remediation detail.
        private static Diagnostic CompileFailed(IReadOnlyList<string> errors) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_009,
                "The script compilation failed.",
                hint: errors.Count == 0
                    ? "Check the Editor log for the compile error."
                    : "Compile errors: " + string.Join(" | ", errors));

        private sealed class TokenState
        {
            internal int ReloadCount;
            internal int CompileCount;
            internal bool SawCompiling;
            internal List<string> Errors = new List<string>(0);

            // Reads the whole wait state out of the resume token. The two
            // counter snapshots are required — without them there is no
            // request to observe completion against; a missing saw-flag or
            // error list is a first-wait token — they default to none.
            internal static TokenState Read(JsonValue token)
            {
                if (token.ToToken() is not JObject obj)
                {
                    throw new ArgumentException(
                        "The resume token is not a ScriptsCompiled token: " +
                        "'reloadCount' and 'compileCount' counts are required.",
                        nameof(token));
                }
                var state = new TokenState
                {
                    ReloadCount = ReadCount(obj, ReloadCountField),
                    CompileCount = ReadCount(obj, CompileCountField),
                };
                if (obj[SawCompilingField] is JValue { Type: JTokenType.Boolean } saw)
                {
                    state.SawCompiling = (bool)saw.Value!;
                }
                if (obj[ErrorsField] is JArray stored)
                {
                    var kept = new List<string>(stored.Count);
                    for (var i = 0;
                        i < stored.Count && kept.Count < MaxCollectedErrors;
                        i++)
                    {
                        if (stored[i] is JValue { Type: JTokenType.String } line)
                        {
                            kept.Add((string)line.Value!);
                        }
                    }
                    state.Errors = kept;
                }
                return state;
            }

            private static int ReadCount(JObject obj, string field)
            {
                if (obj[field] is not JValue { Type: JTokenType.Integer } count ||
                    (long)count.Value! < 0)
                {
                    throw new ArgumentException(
                        "The resume token is not a ScriptsCompiled token: " +
                        $"a non-negative '{field}' count is required.",
                        nameof(obj));
                }
                return (int)(long)count.Value!;
            }
        }
    }
}
