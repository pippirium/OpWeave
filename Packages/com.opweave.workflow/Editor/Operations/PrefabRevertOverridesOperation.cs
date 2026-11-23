#nullable enable

using System;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.prefab.revert_overrides</c> (SPEC §I.2, §G6 item 6): reverts
    /// every override of the referenced Prefab instance back to its source
    /// Prefab through
    /// <see cref="PrefabUtility.RevertPrefabInstance(GameObject, InteractionMode)"/>
    /// with <see cref="InteractionMode.UserAction"/> — the scene-side,
    /// Undoable counterpart of <c>unity.prefab.apply_overrides</c> — and
    /// republishes the instance as a <c>unity.PrefabInstanceRef</c>.
    /// Immediate, Undo <see cref="UndoSupport.Full"/>,
    /// <see cref="Idempotency.Idempotent"/>: an already-reverted instance
    /// reverts cleanly again as a no-op success.
    /// </summary>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class PrefabRevertOverridesOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.prefab.revert_overrides";

        internal const string InstancePort = "instance";

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
            // §D8 item 1: an edge-fed port has no value to inspect in a Dry
            // Run, and a missing required port resolves nothing.
            if (ctx.IsPending(InstancePort) || !ctx.IsPresent(InstancePort))
            {
                return base.Preflight(ctx);
            }
            var inputs = PrefabInstanceEvidence.ResolveInstance(ctx, InstancePort);
            return inputs.Ok
                ? base.Preflight(ctx)
                : new PreflightResult(
                    inputs.Diagnostics, Array.Empty<PlannedChange>(),
                    s_emptySnapshot);
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var inputs = PrefabInstanceEvidence.ResolveInstance(ctx, InstancePort);
            if (!inputs.Ok)
            {
                return StepResult.Failed(inputs.Diagnostics[0]);
            }
            var root = inputs.Value!;
            try
            {
                PrefabUtility.RevertPrefabInstance(
                    root, InteractionMode.UserAction);
            }
            catch (Exception e)
            {
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_007,
                    "The instance's overrides could not be reverted: " +
                    e.GetType().Name,
                    port: InstancePort));
            }
            ctx.Changes.Modified(
                new ChangeTarget(
                    "gameObject",
                    RefHierarchyPath.For(root),
                    null,
                    GlobalObjectId.GetGlobalObjectIdSlow(root).ToString()),
                undoable: true);
            ctx.SetOutput(InstancePort, root);
            return StepResult.Succeeded();
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Revert all overrides of a Prefab instance back to its source Prefab.")
            .Ja(
                "Prefab オーバーライドを取り消し",
                "Prefab インスタンスのオーバーライドをすべて取り消し、元の Prefab の状態に戻す。シーン側の変更で Undo 可能。",
                "Prefab", "オーバーライド", "取り消し")
            .Input(InstancePort, "unity.PrefabInstanceRef", required: true)
            .Output(InstancePort, "unity.PrefabInstanceRef")
            .Effects(EffectNames.SceneModify)
            .Undo(UndoSupport.Full)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Revert Prefab Overrides",
                Description =
                    "Reverts every override the referenced Prefab instance " +
                    "carries — property modifications, added and removed " +
                    "Components and GameObjects — back to its source Prefab " +
                    "through Unity's user-action RevertPrefabInstance and " +
                    "republishes the instance root. The change is a scene " +
                    "modification and fully Undoable; an already-reverted " +
                    "instance reverts cleanly again (Idempotent).",
                Category = "Prefab",
                Tags = new[] { "prefab", "instance", "overrides", "scene" },
                Keywords = new[] { "prefab", "revert", "overrides", "undo" },
                Examples = new[]
                {
                    new OperationExample(
                        "Instantiate a Prefab and revert its overrides",
                        PrefabOverrideOperationExamples.RevertOverrides,
                        PrefabCreateOperationExamples.FixtureManifest),
                },
            };
    }
}
