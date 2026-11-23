#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.asset.save_all</c> (SPEC §I.3, §G7): flushes every pending
    /// asset change to disk through <c>AssetDatabase.SaveAssets</c> — the
    /// §I.3 note names that call. No inputs, no outputs,
    /// <see cref="EffectNames.AssetModify"/>, <see cref="ExecutionMode.Immediate"/>,
    /// Undo <see cref="UndoSupport.None"/> and <see cref="Idempotency.Idempotent"/>:
    /// saving an already-saved Editor is a success that changes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The step records no <c>§F7</c> change. <see cref="StepContext.Changes"/>
    /// takes one target per recorded change and this step's targets are exactly
    /// the assets the Editor happens to hold dirty — a set the Operation does
    /// not enumerate and cannot name without loading every asset in the
    /// project, and §G7 does not ask it to. §I.3 also declares no output for
    /// the operation, so a call that saved nothing and a call that saved
    /// everything look the same to the Plan, which is the point of calling it
    /// last in a workflow.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class SaveAllOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.asset.save_all";

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var guarded = GuardedSave();
            return guarded is null
                ? base.Preflight(ctx)
                : new PreflightResult(
                    new[] { guarded },
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // SPEC §D0.3 item 4: re-checked immediately before the flush,
            // on the main thread Run acceptance serializes on.
            var guarded = GuardedSave();
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }
            AssetDatabase.SaveAssets();
            return StepResult.Succeeded();
        }

        // The SPEC §D0.3 judgment of the sweep (T-W4.2-06): the pending
        // dirty set is exactly what this Operation cannot enumerate, so
        // while any Run is unfinished it cannot prove no definition-bearing
        // file is among it and the flush is refused (OW-RUN-013).
        private static Diagnostic? GuardedSave() =>
            RunDefinitionGuard.CheckUnprovableDefinitionChange(
                "pending asset saves");

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Writes every pending asset change of the Editor to disk (AssetDatabase.SaveAssets).")
            .Ja("アセット保存",
                "エディタが持つ未保存のアセット変更をすべてディスクへ書き出します。",
                "保存", "アセット", "フラッシュ")
            .Effects(EffectNames.AssetModify)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Save Assets",
                Description =
                    "Flushes every asset change the Editor still holds in memory to disk, the " +
                    "way AssetDatabase.SaveAssets does. The operation takes no inputs and " +
                    "produces no outputs: it is the last node of a workflow that changed assets " +
                    "through the Editor API and must leave them on disk. Calling it when " +
                    "nothing is dirty succeeds and changes nothing.",
                Category = "Asset",
                Tags = new[] { "asset", "save" },
                Keywords = new[] { "save", "flush", "dirty" },
                Examples = new[]
                {
                    new OperationExample(
                        "Create a folder, then flush pending asset changes",
                        AssetOperationExamples.SaveAll),
                },
            };
    }
}
