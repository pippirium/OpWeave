#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.scene.close</c> (SPEC §I.1, §G5 item 2): closes the loaded
    /// scene the required <c>scene</c> input names. A scene that is already
    /// closed is a success that changes nothing — the §F5 condition that
    /// makes the Operation <see cref="Idempotency.Idempotent"/>. The last
    /// loaded scene is never closed (§I.1 note): the Editor requires at
    /// least one loaded scene, so the refusal is OW-UNITY-011, the §J code
    /// for a scene state that prevents the operation. Immediate, Undo None.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>dirtyScenes</c> governs a dirty target the way §G5 item 1 does
    /// for open: <c>fail</c> (the default) refuses with OW-UNITY-011,
    /// <c>save</c> saves the scene first — which an untitled scene cannot
    /// be, so it still refuses. Discarding unsaved work is never done.
    /// </para>
    /// <para>
    /// The already-closed success covers every SceneRef whose resolution
    /// answers OW-UNITY-003 — whether the scene was open and closed by an
    /// earlier run of this step or was never loaded: the close outcome the
    /// step exists to bring about already holds. A resolution that fails
    /// differently (OW-UNITY-001/002/OW-TYPE-003) is the failure it reads.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class CloseSceneOperation : OperationBase, IEffectResolver
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.scene.close";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

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
            if (!ctx.IsPresent(SceneOps.ScenePort) || ctx.IsPending(SceneOps.ScenePort))
            {
                return base.Preflight(ctx);
            }
            var failure = Check(ctx, out _, out _);
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
            var failure = Check(ctx, out var scene, out var closed);
            if (failure is not null)
            {
                return StepResult.Failed(failure);
            }
            if (closed)
            {
                // §G5 item 2 / §F5 audit row: already closed is a success.
                return StepResult.Succeeded();
            }
            if (scene.isDirty)
            {
                failure = SceneOps.ApplyDirtyScenesRule(
                    ctx, new List<Scene> { scene });
                if (failure is not null)
                {
                    return StepResult.Failed(failure);
                }
            }
            if (!EditorSceneManager.CloseScene(scene, removeScene: true))
            {
                return StepResult.Failed(SceneOps.SceneStateBlocked(
                    "The scene could not be closed."));
            }
            return StepResult.Succeeded();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The §I.1 dirtyScenes=save case saves the scene before closing
        /// it, so the actual effects of a save-capable node include
        /// <c>scene.save</c> the same way <c>unity.scene.open</c>'s do
        /// (§C4's IEffectResolver contract).
        /// </remarks>
        public IReadOnlyList<string> ResolveEffects(StaticContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            return OpenSceneOperation.MaySave(context.Parameters)
                ? new[] { EffectNames.SceneLoad, EffectNames.SceneSave }
                : new[] { EffectNames.SceneLoad };
        }

        // The precondition chain of Preflight and Execute: the required
        // scene ref (whose OW-UNITY-003 resolution is the already-closed
        // success, not a failure) and the last-loaded-scene refusal —
        // closing it would leave the Editor without a loaded scene, which
        // EditorSceneManager forbids (§I.1 note).
        private static Diagnostic? Check(StepContext ctx, out Scene scene, out bool closed)
        {
            closed = false;
            try
            {
                scene = ctx.Get<Scene>(SceneOps.ScenePort);
            }
            catch (StepContextException error)
            {
                scene = default;
                var notLoaded = error.Diagnostics.Count > 0;
                for (var i = 0; i < error.Diagnostics.Count; i++)
                {
                    notLoaded &= string.Equals(
                        error.Diagnostics[i].Code,
                        ErrorCodes.OW_UNITY_003, StringComparison.Ordinal);
                }
                if (notLoaded)
                {
                    closed = true;
                    return null;
                }
                return error.Diagnostics.Count > 0
                    ? error.Diagnostics[0]
                    : SceneOps.SceneStateBlocked(
                        "The scene reference could not be resolved.");
            }
            if (SceneOps.LoadedScenes().Count <= 1)
            {
                return SceneOps.SceneStateBlocked(
                    "The last loaded scene cannot be closed.",
                    hint: "Open another scene first.");
            }
            if (scene.isDirty)
            {
                // A pending dirtyScenes is edge-fed (SPEC §D8 item 1): it
                // takes the "fail" default like a missing port.
                var mode = ctx.IsPresent(SceneOps.DirtyScenesPort) &&
                    !ctx.IsPending(SceneOps.DirtyScenesPort)
                    ? ctx.Get<string>(SceneOps.DirtyScenesPort)
                    : SceneOps.DirtyScenesFail;
                if (!string.Equals(mode, SceneOps.DirtyScenesSave, StringComparison.Ordinal))
                {
                    return SceneOps.SceneStateBlocked(
                        "Unsaved scenes prevent the operation.",
                        SceneOps.DirtyScenesPort,
                        "Save the scene first, or set 'dirtyScenes' to 'save'.");
                }
                if (scene.path.Length == 0)
                {
                    return SceneOps.SceneStateBlocked(
                        "An unsaved scene has no path to be saved to.",
                        SceneOps.DirtyScenesPort,
                        "Save the untitled scene under a path first.");
                }
            }
            return null;
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Closes a loaded scene; an already closed scene succeeds, the last scene is never closed.")
            .Ja("シーンを閉じる",
                "読み込んでいるシーンを閉じます。既に閉じていれば成功、最後の1つは閉じられません。",
                "シーン", "閉じる", "アンロード")
            .Input(SceneOps.ScenePort, "unity.SceneRef", required: true)
            .Input(SceneOps.DirtyScenesPort, "core.String", required: false,
                c => c.Enum(SceneOps.DirtyScenesFail, SceneOps.DirtyScenesSave),
                @default: s_defaultDirtyScenes)
            .Effects(EffectNames.SceneLoad)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Close Scene",
                Description =
                    "Closes the loaded scene the 'scene' input names. Closing a scene that " +
                    "is already closed succeeds and changes nothing. A dirty target refuses " +
                    "with OW-UNITY-011 unless 'dirtyScenes' is 'save', which saves it first; " +
                    "an untitled dirty scene cannot be saved and still refuses, and the last " +
                    "loaded scene is never closed — unsaved work is never discarded.",
                Category = "Scene",
                Tags = new[] { "scene", "close" },
                Keywords = new[] { "scene", "close", "unload" },
                Examples = new[]
                {
                    new OperationExample(
                        "Open a scene additively and close it again",
                        SceneOperationExamples.CloseScene),
                },
            };
    }
}
