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
    /// <c>unity.scene.save</c> (SPEC §I.1, §G5 item 3): saves a loaded scene —
    /// the <c>scene</c> input's target, or the active scene when the port is
    /// omitted — to its own path or to <c>path</c> when given. Immediate,
    /// Undo None, <see cref="Idempotency.Idempotent"/>: saving an
    /// already-saved scene is a success that changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §G5 item 3: an Untitled scene has no path of its own, so saving one
    /// without <c>path</c> is refused (OW-UNITY-005, the §J path code). A
    /// <c>path</c> that is already occupied by a different asset is also
    /// OW-UNITY-005 — saving must never overwrite another scene file; a
    /// local, conservative choice where SPEC is silent. The write half of
    /// the given path is judged by <see cref="AssetPathPolicy.CheckWritePath"/>
    /// and the port's §G1 <c>path_writable</c> requirement.
    /// </para>
    /// <para>
    /// A <c>scene</c> input that resolves to no loaded scene fails with
    /// OW-UNITY-003 through the context's §G2 SceneRef resolution — a scene
    /// that is not loaded cannot be saved.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class SaveSceneOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.scene.save";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // §D8 item 1: edge-fed ports have no value to inspect in a Dry Run.
            if (ctx.IsPending(SceneOps.ScenePort) || ctx.IsPending(SceneOps.PathPort))
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
            var failure = Check(ctx, out var scene);
            if (failure is not null)
            {
                return StepResult.Failed(failure);
            }

            var saved = ctx.IsPresent(SceneOps.PathPort)
                ? EditorSceneManager.SaveScene(
                    scene,
                    AssetPathPolicy.Normalize(ctx.Get<string>(SceneOps.PathPort))!)
                : EditorSceneManager.SaveScene(scene);
            if (!saved)
            {
                return StepResult.Failed(SceneOps.InvalidPath(
                    SceneOps.PathPort,
                    "The scene could not be saved.",
                    "Choose a path inside an existing writable folder."));
            }

            var path = scene.path;
            ctx.Changes.Saved(
                new ChangeTarget(
                    "asset", path,
                    AssetDatabase.AssetPathToGUID(path)),
                undoable: false);
            ctx.SetOutput(SceneOps.ScenePort, SceneOps.SceneRefOf(path));
            return StepResult.Succeeded();
        }

        // The precondition chain of Preflight and Execute: the target scene
        // (resolved through the §G2 SceneRef order, or the active scene),
        // the optional write path and the Untitled rule — the
        // different-asset occupancy refusal stays with the shared check.
        private static Diagnostic? Check(StepContext ctx, out Scene scene)
        {
            if (ctx.IsPresent(SceneOps.ScenePort))
            {
                Scene resolved;
                try
                {
                    resolved = ctx.Get<Scene>(SceneOps.ScenePort);
                }
                catch (StepContextException error)
                {
                    scene = default;
                    return error.Diagnostics[0];
                }
                scene = resolved;
            }
            else
            {
                scene = SceneManager.GetActiveScene();
            }
            string? path = null;
            if (ctx.IsPresent(SceneOps.PathPort))
            {
                var write = AssetPathPolicy.CheckWritePath(
                    ctx.Get<string>(SceneOps.PathPort), SceneOps.PathPort);
                if (!write.Ok)
                {
                    return write.Diagnostics[0];
                }
                path = write.Value!;
                // A destination that is already an asset other than this
                // scene's own file is never overwritten: OW-UNITY-005, the
                // §J code for a collision with an existing asset.
                if (!string.Equals(path, scene.path, StringComparison.Ordinal) &&
                    SceneOps.Occupied(SceneOps.ToPhysicalPath(path), path))
                {
                    return SceneOps.InvalidPath(
                        SceneOps.PathPort,
                        "The path already names another asset and is never overwritten.",
                        "Choose a scene path that does not exist yet.");
                }
            }
            // §G5 item 3: an Untitled scene names no file to save into.
            if (scene.path.Length == 0 && path is null)
            {
                return SceneOps.InvalidPath(
                    SceneOps.PathPort,
                    "Saving an untitled scene requires a path.",
                    "Pass the 'path' input the scene is saved under.");
            }
            return null;
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Saves a loaded scene to its own path or to a given path; an untitled scene needs a path.")
            .Ja("シーン保存",
                "読み込んでいるシーンを保存します。Untitled のシーンには保存先パスが必要です。",
                "シーン", "保存", "書き出し")
            .Input(SceneOps.ScenePort, "unity.SceneRef", required: false)
            .Input(SceneOps.PathPort, "unity.AssetPath", required: false)
            .Output(SceneOps.ScenePort, "unity.SceneRef")
            .Effects(EffectNames.SceneSave)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            // SPEC §G1 item 4: the optional save destination's allowed-paths
            // and writability half — satisfied while the port carries no
            // value, judged when one is supplied.
            .Requires("path_writable", SceneOps.PathPort)
            .Build() with
            {
                DisplayName = "Save Scene",
                Description =
                    "Saves the given loaded scene — or the active scene when 'scene' is " +
                    "omitted — to its own path, or to 'path' when one is given (Save As). " +
                    "An untitled scene has no path of its own, so saving one without " +
                    "'path' fails with OW-UNITY-005; a 'path' already occupied by another " +
                    "asset is never overwritten and fails the same way, and a scene that " +
                    "is not loaded fails with OW-UNITY-003.",
                Category = "Scene",
                Tags = new[] { "scene", "save" },
                Keywords = new[] { "scene", "save", "write" },
                Examples = new[]
                {
                    new OperationExample(
                        "Create a scene and save it under a new path",
                        SceneOperationExamples.SaveScene),
                },
            };
    }
}
