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
    /// <c>unity.prefab.apply_overrides</c> (SPEC §I.2, §G6 item 5): applies
    /// every override of the referenced Prefab instance to its source
    /// Prefab asset through
    /// <see cref="PrefabUtility.ApplyPrefabInstance(GameObject, InteractionMode)"/>
    /// with <see cref="InteractionMode.AutomatedAction"/> and saves the
    /// asset, publishing the modified Prefab as a
    /// <c>unity.PrefabAssetRef</c>. The shared asset file itself changes,
    /// so the Operation is destructive with Undo
    /// <see cref="UndoSupport.None"/> and the §I.2
    /// <see cref="Idempotency.Idempotent"/> classification — a repeated
    /// application of an unchanged instance is a no-op success. Immediate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §G6 apply workflow — create → instantiate → component.add →
    /// apply_overrides — leaves the added Component inside the Prefab
    /// asset; the asset.modify change record carries
    /// <c>undoable:false</c> because Unity offers no Undo for an
    /// AutomatedAction apply (SPEC §I.2).
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class PrefabApplyOverridesOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.prefab.apply_overrides";

        internal const string InstancePort = "instance";
        internal const string PrefabPort = "prefab";

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
                    JsonValue.From(new JObject()));
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
            var prefabPath = PrefabCreateEvidence.NormalizedInstancePath(root);
            if (prefabPath.Length == 0)
            {
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_004,
                    "The Prefab instance's source Prefab could not be identified.",
                    port: InstancePort));
            }
            try
            {
                PrefabUtility.ApplyPrefabInstance(
                    root, InteractionMode.AutomatedAction);
            }
            catch (Exception e)
            {
                return StepResult.Failed(PrefabCreateEvidence.WriteFailed(
                    InstancePort,
                    "the instance's overrides could not be applied: " +
                    e.GetType().Name));
            }
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            ctx.Changes.Modified(
                new ChangeTarget("asset", prefabPath, guid), undoable: false);
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (asset == null)
            {
                return StepResult.Failed(PrefabCreateEvidence.WriteFailed(
                    InstancePort,
                    "the Prefab asset could not be loaded after the apply"));
            }
            AssetDatabase.SaveAssetIfDirty(asset);
            ctx.Changes.Saved(
                new ChangeTarget("asset", prefabPath, guid), undoable: false);
            ctx.SetOutput(PrefabPort, asset);
            return StepResult.Succeeded();
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Apply all overrides of a Prefab instance to its source Prefab asset.")
            .Ja(
                "Prefab オーバーライドを適用",
                "Prefab インスタンスのオーバーライドを元の Prefab アセットへ適用し保存する。Prefab アセット自体を書き換える破壊的操作で Undo 不可。",
                "Prefab", "オーバーライド", "適用")
            .Input(InstancePort, "unity.PrefabInstanceRef", required: true)
            .Output(PrefabPort, "unity.PrefabAssetRef")
            .Effects(EffectNames.AssetModify)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Apply Prefab Overrides",
                Description =
                    "Applies every override the referenced Prefab instance " +
                    "carries to its source Prefab asset through Unity's " +
                    "automated ApplyPrefabInstance and saves the asset. The " +
                    "operation rewrites the shared Prefab file itself, so it " +
                    "is destructive and not Undoable; an unchanged instance " +
                    "applies cleanly again (Idempotent).",
                Category = "Prefab",
                Tags = new[] { "prefab", "instance", "overrides", "asset" },
                Keywords = new[] { "prefab", "apply", "overrides", "save" },
                Destructive = true,
                Examples = new[]
                {
                    new OperationExample(
                        "Add a component to an instance and apply the override",
                        PrefabOverrideOperationExamples.ApplyOverrides,
                        PrefabCreateOperationExamples.FixtureManifest),
                },
            };
    }
}
