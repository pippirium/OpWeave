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
    /// <c>unity.gameobject.set_parent</c> (SPEC §I.1): reparents the target
    /// GameObject under the given parent — or to its scene's root when none
    /// is given — keeping the world transform unless
    /// <c>worldPositionStays</c> is false. A cyclic reparent (the parent is
    /// the target itself or one of its descendants) is OW-UNITY-007. Undo
    /// <see cref="UndoSupport.Full"/>, <see cref="Idempotency.Idempotent"/>:
    /// a target already under the requested parent succeeds and records no
    /// change.
    /// </summary>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class GameObjectSetParentOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.gameobject.set_parent";

        internal const string TargetPort = "target";
        internal const string ParentPort = "parent";
        internal const string WorldPositionStaysPort = "worldPositionStays";
        internal const string GameObjectPort = "gameObject";

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (ctx.IsPending(TargetPort) || ctx.IsPending(ParentPort))
            {
                return base.Preflight(ctx);
            }
            try
            {
                ctx.Get<GameObject>(TargetPort);
                if (ctx.IsPresent(ParentPort))
                {
                    ctx.Get<GameObject>(ParentPort);
                }
            }
            catch (StepContextException failure)
            {
                return new PreflightResult(
                    failure.Diagnostics,
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));
            }
            return base.Preflight(ctx);
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var target = ctx.Get<GameObject>(TargetPort);
            var parent = ctx.IsPresent(ParentPort)
                ? ctx.Get<GameObject>(ParentPort)
                : null;
            if (parent is not null &&
                parent.scene.handle != target.scene.handle)
            {
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_002,
                    "The parent GameObject is not in the target's scene.",
                    port: ParentPort));
            }
            // The cyclic-parent refusal of the §I.1 note: the target itself
            // or one of its descendants can never become its parent.
            if (parent is not null &&
                (ReferenceEquals(parent, target) ||
                    parent.transform.IsChildOf(target.transform)))
            {
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_007,
                    "The parent is the target or one of its descendants; " +
                    "the reparent would create a cycle.",
                    port: ParentPort));
            }
            var worldPositionStays = !ctx.IsPresent(WorldPositionStaysPort) ||
                ctx.Get<bool>(WorldPositionStaysPort);

            var parentTransform = parent is null ? null : parent.transform;
            if (!ReferenceEquals(target.transform.parent, parentTransform))
            {
                Undo.SetTransformParent(
                    target.transform, parentTransform, worldPositionStays,
                    "Set Parent of " + target.name);
                ctx.Changes.Moved(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(target),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(target).ToString()),
                    undoable: true);
            }
            ctx.SetOutput(GameObjectPort, target);
            return StepResult.Succeeded();
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Reparents a GameObject under another, or to the scene root; cyclic parents are refused.")
            .Ja("親の設定",
                "GameObject を別の GameObject の子（省略時はルート）に移します。循環する親子は拒否します。",
                "GameObject", "親", "階層")
            .Input(TargetPort, "unity.GameObjectRef", required: true)
            .Input(ParentPort, "unity.GameObjectRef", required: false)
            .Input(WorldPositionStaysPort, "core.Bool", required: false,
                @default: JsonValue.From(new JValue(true)))
            .Output(GameObjectPort, "unity.GameObjectRef")
            .Effects(EffectNames.SceneModify)
            .Undo(UndoSupport.Full)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Set Parent",
                Description =
                    "Moves the target GameObject under the given parent, or to the " +
                    "scene's root when no parent is given. worldPositionStays (default " +
                    "true) keeps the object's world position, rotation and scale. " +
                    "Reparenting under the target itself or one of its descendants " +
                    "fails with OW-UNITY-007; a parent in a different scene fails " +
                    "with OW-UNITY-002.",
                Category = "GameObject",
                Tags = new[] { "gameObject", "scene", "hierarchy" },
                Keywords = new[] { "gameobject", "parent", "reparent", "hierarchy", "move" },
                Examples = new[]
                {
                    new OperationExample(
                        "Parent the created child under the created root",
                        GameObjectOperationExamples.SetParent),
                },
            };
    }
}
