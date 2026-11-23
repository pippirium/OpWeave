#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor.Compilation;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.script.compile</c> (SPEC §I.4, §G8 item 3): requests a full
    /// script compilation through
    /// <see cref="CompilationPipeline.RequestScriptCompilation"/> and waits
    /// on the SPEC §G4 <see cref="ScriptsCompiledCondition"/> until the
    /// compile is observed complete — a compile-finished event, the
    /// no-change not-required event or the domain reload the compile
    /// caused. <see cref="ExecutionMode.Waitable"/> with the SPEC row's
    /// 600-second default timeout, Undo <see cref="UndoSupport.None"/> and
    /// <see cref="Idempotency.Idempotent"/>: requesting the same
    /// compilation again is the same step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The wait.</b> Execute opens the step's §G4 capture windows —
    /// the error list <see cref="ScriptsCompiledCondition.InitialToken"/>
    /// arms and the warning mirror <see cref="CompileWarningCapture"/> —
    /// before the request is queued, so a compile that reports before the
    /// first <see cref="Poll"/> still attributes its messages to this
    /// step. Poll asks the condition and nothing else; the whole wait
    /// state lives in the resume token (SPEC §C2 item 4), which the
    /// condition restores across the domain reload a successful compile
    /// triggers. A failed compile —
    /// <see cref="UnityEditor.EditorUtility.scriptCompilationFailed"/>
    /// once the Editor is idle — fails the step with OW-RUN-009 carrying
    /// the captured <c>file(line,column)</c> error lines in its hint; the
    /// same lines are journaled on the step as <c>unity_compile</c>
    /// records by the capture (SPEC §F6), which is what
    /// <c>get_run_logs</c> surfaces.
    /// </para>
    /// <para>
    /// <b>The <c>result</c> payload.</b> On success the output is the
    /// §I.4 Json <c>{ "errors": [...], "warnings": [...] }</c> — the
    /// compile errors the token folded in (empty when the compile passed)
    /// and the captured warning lines, which travel inside the resume
    /// token so they survive the domain reload the compile caused. The
    /// errors the token carries also back a Failed verdict's hint, so no
    /// line is lost to a reload.
    /// </para>
    /// <para>
    /// <b>No continuation across the reload.</b> A successful compile
    /// reloads the domain; the Run picks the wait back up from the
    /// persisted resume token afterwards (SPEC §E4). Making a later step
    /// consume a generated type from the same Run is a separate contract
    /// (SPEC §M.7, T-W4.3-03) this Operation does not implement.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class CompileScriptsOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.script.compile";

        /// <summary>The name of the compilation report output port.</summary>
        internal const string ResultPort = "result";

        /// <summary>The fixed English label journaled as StepWaiting waitingFor (SPEC §F2).</summary>
        internal const string WaitingForLabel = "script compilation";

        private const string ErrorsField = "errors";
        private const string WarningsField = "warnings";
        private const int MaxCollectedWarnings = 16;

        private readonly Action _requestCompilation;
        private readonly IWaitCondition _condition;

        /// <summary>Creates the Operation over the live compilation pipeline.</summary>
        public CompileScriptsOperation()
            : this(
                  CompilationPipeline.RequestScriptCompilation,
                  new ScriptsCompiledCondition())
        {
        }

        /// <summary>
        /// The test seams over the queued compile request and the wait
        /// condition — the <see cref="EditorStateProbe"/> pattern, so the
        /// Waitable lifecycle is provable without driving a real compile.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal CompileScriptsOperation(
            Action requestCompilation, IWaitCondition condition)
        {
            _requestCompilation = requestCompilation ??
                throw new ArgumentNullException(nameof(requestCompilation));
            _condition = condition ??
                throw new ArgumentNullException(nameof(condition));
        }

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // The capture windows open before the request: a compile that
            // finishes (or fails) before the first Poll still attributes
            // its errors and warnings to this step.
            CompileWarningCapture.BeginStep();
            var token = ScriptsCompiledCondition.InitialToken();
            _requestCompilation();
            return StepResult.Waiting(token, WaitingForLabel);
        }

        /// <inheritdoc/>
        public override StepResult Poll(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var token = ctx.ResumeToken ?? throw new InvalidOperationException(
                "A Waitable Operation keeps its wait state only in the resume " +
                "token (SPEC §C2 item 4); none was handed back.");
            // A wait continued from its persisted token still owns its
            // warning window — the arm ran in the domain the reload dropped.
            CompileWarningCapture.ResumeWait();
            var warnings = CollectedWarnings(token);
            var status = _condition.Check(token);
            switch (status.Kind)
            {
                case WaitStatusKind.Done:
                    CompileWarningCapture.EndStep();
                    ctx.SetOutput(ResultPort, JsonValue.From(new JObject
                    {
                        [ErrorsField] = ErrorsOf(token),
                        [WarningsField] = new JArray(warnings),
                    }));
                    return StepResult.Succeeded();
                case WaitStatusKind.Failed:
                    CompileWarningCapture.EndStep();
                    return StepResult.Failed(
                        status.Error ?? throw new InvalidOperationException(
                            "A Failed WaitStatus carries its Diagnostic (SPEC §G4)."));
                default:
                    // The condition rewrites the token on every Pending
                    // check, so the folded warnings are injected back —
                    // the token is the only state that survives a reload.
                    return StepResult.Waiting(
                        WithWarnings(status.ResumeToken!, warnings), WaitingForLabel);
            }
        }

        /// <inheritdoc/>
        public override void Cancel(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            ScriptsCompiledCondition.EndWait();
            CompileWarningCapture.EndStep();
        }

        // The wait's warnings, deduped and bounded: the entries the token
        // already folded plus the capture's newer ones — the same monotone
        // merge the condition applies to errors, which is what keeps them
        // across a domain reload.
        private static List<string> CollectedWarnings(JsonValue token)
        {
            var warnings = new List<string>();
            if (token.ToToken() is JObject obj &&
                obj[WarningsField] is JArray stored)
            {
                for (var i = 0;
                    i < stored.Count && warnings.Count < MaxCollectedWarnings;
                    i++)
                {
                    if (stored[i] is JValue { Type: JTokenType.String } line)
                    {
                        warnings.Add((string)line.Value!);
                    }
                }
            }
            var recent = CompileWarningCapture.RecentWarnings;
            for (var i = 0;
                i < recent.Count && warnings.Count < MaxCollectedWarnings;
                i++)
            {
                if (!warnings.Contains(recent[i]))
                {
                    warnings.Add(recent[i]);
                }
            }
            return warnings;
        }

        private static JArray ErrorsOf(JsonValue token)
        {
            var errors = new JArray();
            if (token.ToToken() is JObject obj && obj[ErrorsField] is JArray stored)
            {
                foreach (var entry in stored)
                {
                    if (entry is JValue { Type: JTokenType.String })
                    {
                        errors.Add(entry);
                    }
                }
            }
            return errors;
        }

        private static JsonValue WithWarnings(JsonValue token, IReadOnlyList<string> warnings)
        {
            var obj = (JObject)token.ToToken().DeepClone();
            obj[WarningsField] = new JArray(warnings);
            return JsonValue.From(obj);
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Requests a script compilation and waits for it to finish.")
            .Ja("スクリプトコンパイル",
                "スクリプトのコンパイルを要求し、完了まで待ちます。",
                "コンパイル", "スクリプト", "ドメインリロード")
            .Output(ResultPort, "core.Json")
            .Effects(EffectNames.Compile)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                ExecutionMode = ExecutionMode.Waitable,
                // The SPEC §I.4 row's own timeout; equal to the builder's
                // Waitable default, pinned so the row is explicit.
                DefaultTimeoutSeconds = 600,
                DisplayName = "Compile Scripts",
                Description =
                    "Requests a full script compilation with " +
                    "CompilationPipeline.RequestScriptCompilation and waits on the " +
                    "SPEC §G4 ScriptsCompiled condition: the wait completes when " +
                    "compilation completion is observed — a compilation-finished " +
                    "or assembly-compilation-not-required event, or the domain " +
                    "reload the compile causes — and resumes from its persisted " +
                    "resume token across that reload. A failed compilation fails " +
                    "the step with OW-RUN-009 carrying the captured " +
                    "file(line,column) error lines, which are also journaled on " +
                    "the step (SPEC §F6). On success the 'result' output is the " +
                    "Json { errors, warnings } report of the compile. The " +
                    "Operation is Idempotent: requesting the same compilation " +
                    "again is the same step.",
                Category = "Script",
                Tags = new[] { "script", "compile" },
                Keywords = new[] { "compile", "script", "domain", "reload" },
                Examples = new[]
                {
                    new OperationExample(
                        "Create a script from a template and compile it",
                        ScriptOperationExamples.CompileScript),
                },
            };
    }
}
