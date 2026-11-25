#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.scene.open</c> (SPEC §I.1, §G5 item 1): opens the scene at
    /// <c>path</c>, replacing the loaded scenes in <c>single</c> mode
    /// (default) or adding to them in <c>additive</c> mode. A scene that is
    /// already open is a no-op success, which is what makes the Operation
    /// <see cref="Idempotency.Idempotent"/>. Immediate, Undo None.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §G5 item 1: opening single-mode while unsaved scenes are open is
    /// governed by <c>dirtyScenes</c> — <c>fail</c> (the default) refuses
    /// with OW-UNITY-011, <c>save</c> saves every dirty loaded scene first
    /// (the +scene.save actual effect, reported through
    /// <see cref="IEffectResolver"/>) and an untitled dirty scene still
    /// refuses because it has no path to save to. Discarding unsaved work
    /// is never an option (§G5 item 1 "破棄して開く選択肢は置かない").
    /// </para>
    /// <para>
    /// The path is a read — opening never writes to it — so its shape is
    /// judged by <see cref="AssetPathPolicy.CheckReadPath"/> (a
    /// <c>..</c>/rooted spelling is OW-PERM-002) and a path that names no
    /// scene asset is OW-UNITY-005 (§J "invalid path or conflicting
    /// existing asset").
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class OpenSceneOperation : OperationBase, IEffectResolver
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.scene.open";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        private static readonly JsonValue s_defaultMode =
            JsonValue.From(new JValue(SceneOps.ModeSingle));

        private static readonly JsonValue s_defaultDirtyScenes =
            JsonValue.From(new JValue(SceneOps.DirtyScenesFail));

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // SPEC §D8 item 1: an edge-fed port has no value to inspect.
            if (!ctx.IsPresent(SceneOps.PathPort) || ctx.IsPending(SceneOps.PathPort))
            {
                return base.Preflight(ctx);
            }
            var failure = Check(ctx, out _);
            return failure is null
                ? base.Preflight(ctx)
                : new PreflightResult(
                    new[] { failure }, Array.Empty<PlannedChange>(), s_emptySnapshot);
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var failure = Check(ctx, out var single);
            if (failure is not null)
            {
                return StepResult.Failed(failure);
            }

            var path = AssetPathPolicy.Normalize(ctx.Get<string>(SceneOps.PathPort))!;
            var already = SceneManager.GetSceneByPath(path);
            if (already.isLoaded)
            {
                // §I.1 note: already open means there is nothing to do —
                // the idempotent outcome, before the dirty-scene rule.
                ctx.SetOutput(SceneOps.ScenePort, SceneOps.SceneRefOf(path));
                return StepResult.Succeeded();
            }
            if (single)
            {
                failure = SceneOps.ApplyDirtyScenesRule(ctx, SceneOps.DirtyScenes());
                if (failure is not null)
                {
                    return StepResult.Failed(failure);
                }
            }

            var scene = EditorSceneManager.OpenScene(
                path,
                single ? OpenSceneMode.Single : OpenSceneMode.Additive);
            ctx.SetOutput(SceneOps.ScenePort, SceneOps.SceneRefOf(scene.path));
            return StepResult.Succeeded();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The §I.1 effects column's "(save 指定時 +scene.save)": a constant
        /// <c>dirtyScenes: "fail"</c> keeps the declared
        /// <c>{scene.load}</c>; a constant <c>"save"</c> adds
        /// <c>scene.save</c>, and a value that is not statically known —
        /// edge-fed or missing the constant — reports both, the
        /// permission-side superset of what the step may do.
        /// </remarks>
        public IReadOnlyList<string> ResolveEffects(StaticContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            return MaySave(context.Parameters)
                ? new[] { EffectNames.SceneLoad, EffectNames.SceneSave }
                : new[] { EffectNames.SceneLoad };
        }

        // True unless the constant dirtyScenes value is provably "fail":
        // an absent port takes the "fail" default, a present non-string or
        // "save" value may save.
        internal static bool MaySave(IReadOnlyDictionary<string, InputValue> parameters)
        {
            if (!parameters.TryGetValue(SceneOps.DirtyScenesPort, out var input) ||
                !input.IsPresent)
            {
                return false;
            }
            var token = input.Value!.ToToken();
            return token.Type != JTokenType.String ||
                string.Equals(
                    (string?)((JValue)token).Value,
                    SceneOps.DirtyScenesSave, StringComparison.Ordinal);
        }

        // The precondition chain of Preflight and Execute: path shape and
        // existence first, then whether the single-mode opening applies.
        private static Diagnostic? Check(StepContext ctx, out bool single)
        {
            var read = AssetPathPolicy.CheckReadPath(
                ctx.Get<string>(SceneOps.PathPort), SceneOps.PathPort);
            if (!read.Ok)
            {
                single = false;
                return read.Diagnostics[0];
            }
            var path = read.Value!;
            // SPEC §D8 item 1: a pending edge-fed port has no value to
            // inspect on the Dry-Run path, so it takes the declared default
            // exactly like a missing port does.
            single = string.Equals(
                ctx.IsPresent(SceneOps.ModePort) &&
                    !ctx.IsPending(SceneOps.ModePort)
                    ? ctx.Get<string>(SceneOps.ModePort)
                    : SceneOps.ModeSingle,
                SceneOps.ModeSingle, StringComparison.Ordinal);
            if (SceneManager.GetSceneByPath(path).isLoaded)
            {
                return null;
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
            {
                return SceneOps.InvalidPath(
                    SceneOps.PathPort,
                    "The path does not name an existing scene asset.",
                    "Choose the path of a .unity asset in the project.");
            }
            if (single && SceneOps.DirtyScenes().Count > 0)
            {
                // A pending dirtyScenes is edge-fed (§D8): the "fail"
                // default answers the check it cannot be read for.
                var mode = ctx.IsPresent(SceneOps.DirtyScenesPort) &&
                    !ctx.IsPending(SceneOps.DirtyScenesPort)
                    ? ctx.Get<string>(SceneOps.DirtyScenesPort)
                    : SceneOps.DirtyScenesFail;
                if (!string.Equals(mode, SceneOps.DirtyScenesSave, StringComparison.Ordinal))
                {
                    return SceneOps.SceneStateBlocked(
                        "Unsaved scenes prevent the operation.",
                        SceneOps.DirtyScenesPort,
                        "Save the open scenes first, or set 'dirtyScenes' to 'save'.");
                }
                foreach (var dirty in SceneOps.DirtyScenes())
                {
                    if (dirty.path.Length == 0)
                    {
                        return SceneOps.SceneStateBlocked(
                            "An unsaved scene has no path to be saved to.",
                            SceneOps.DirtyScenesPort,
                            "Save the untitled scene under a path first.");
                    }
                }
            }
            return null;
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Opens a scene, replacing the loaded scenes in single mode or adding to them in additive mode.")
            .Ja("シーンを開く",
                "シーンを開きます。single モードでは開いているシーンを置き換え、additive では追加します。",
                "シーン", "開く", "読み込み")
            .Input(SceneOps.PathPort, "unity.AssetPath", required: true)
            .Input(SceneOps.ModePort, "core.String", required: false,
                c => c.Enum(SceneOps.ModeSingle, SceneOps.ModeAdditive),
                @default: s_defaultMode)
            .Input(SceneOps.DirtyScenesPort, "core.String", required: false,
                c => c.Enum(SceneOps.DirtyScenesFail, SceneOps.DirtyScenesSave),
                @default: s_defaultDirtyScenes)
            .Output(SceneOps.ScenePort, "unity.SceneRef")
            .Effects(EffectNames.SceneLoad)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Open Scene",
                Description =
                    "Opens the scene at the given project asset path. In 'single' mode " +
                    "(the default) it replaces the loaded scenes: unsaved scenes refuse " +
                    "the step with OW-UNITY-011 unless 'dirtyScenes' is 'save', which " +
                    "saves every dirty scene first; discarding unsaved work is never " +
                    "done. In 'additive' mode the scene is loaded beside the open ones. " +
                    "A scene that is already open succeeds and changes nothing; a path " +
                    "that names no scene asset fails with OW-UNITY-005.",
                Category = "Scene",
                Tags = new[] { "scene", "open" },
                Keywords = new[] { "scene", "open", "load" },
                Examples = new[]
                {
                    new OperationExample(
                        "Open the main scene additively",
                        SceneOperationExamples.OpenScene),
                },
            };
    }
}
