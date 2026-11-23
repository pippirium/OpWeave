#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.gameobject.delete</c> (SPEC §I.1): destroys the target
    /// GameObject and its hierarchy. Destructive, Undo
    /// <see cref="UndoSupport.Full"/>, <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §F5 evidence row: Preflight records the target's handle,
    /// globalId, scene, hierarchy path, parent, sibling position, the
    /// parent's ordered children and the session id. Reconcile in the same
    /// session returns <see cref="ReconcileResult.Completed"/> only when
    /// the target no longer resolves and the parent's children equal the
    /// snapshot minus the target — every other object still there —
    /// <see cref="ReconcileResult.NotApplied"/> when the target is still
    /// present with its recorded identity and the child list is unchanged,
    /// and <see cref="ReconcileResult.Unknown"/> otherwise (another object
    /// gone, the target moved, a different session).
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class GameObjectDeleteOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.gameobject.delete";

        internal const string TargetPort = "target";

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
            if (ctx.IsPending(TargetPort))
            {
                return base.Preflight(ctx);
            }
            GameObject target;
            try
            {
                target = ctx.Get<GameObject>(TargetPort);
            }
            catch (StepContextException failure)
            {
                return new PreflightResult(
                    failure.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var parent = target.transform.parent;
            var snapshot = new JObject
            {
                ["scene"] = GameObjectEvidence.SceneKey(target.scene),
                ["target"] = GameObjectEvidence.RefOf(target),
                ["parent"] = parent is null
                    ? JValue.CreateNull()
                    : GameObjectEvidence.RefOf(parent.gameObject),
                ["siblingIndex"] = target.transform.GetSiblingIndex(),
                ["children"] = parent is null
                    ? GameObjectEvidence.ChildrenOf(target.scene)
                    : GameObjectEvidence.ChildrenOf(parent),
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            return new PreflightResult(
                Array.Empty<Diagnostic>(),
                Array.Empty<PlannedChange>(),
                JsonValue.From(snapshot));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var target = ctx.Get<GameObject>(TargetPort);
            var path = RefHierarchyPath.For(target);
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();

            Undo.DestroyObjectImmediate(target);

            ctx.Changes.Deleted(
                new ChangeTarget("gameObject", path, null, globalId),
                undoable: true);
            return StepResult.Succeeded();
        }

        /// <inheritdoc/>
        public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (snapshot.ToToken() is not JObject obj)
            {
                return GameObjectEvidence.Unknown("The Preflight snapshot is not an object.");
            }
            if (obj["scene"] is not JObject sceneKey ||
                obj["target"] is not JObject targetRef ||
                obj["parent"] is not { } parentToken ||
                obj["children"] is not JArray children ||
                obj["editorSessionId"] is not JValue { Type: JTokenType.String } sessionToken)
            {
                return GameObjectEvidence.Unknown(
                    "The Preflight snapshot is missing a required evidence member.");
            }
            if (!string.Equals(
                (string)sessionToken.Value!, EditorSessionId.GetOrCreate(),
                StringComparison.Ordinal))
            {
                return GameObjectEvidence.Unknown(
                    "The snapshot belongs to a different Editor session.");
            }
            var targetRaw = GameObjectEvidence.HandleOf(targetRef);
            if (targetRaw is null)
            {
                return GameObjectEvidence.Unknown(
                    "The snapshot's target carries no session handle.");
            }
            var targetGlobalId = targetRef["globalId"] is JValue { Type: JTokenType.String } id
                ? (string?)id.Value
                : null;

            var scene = GameObjectEvidence.ResolveScene(sceneKey);
            if (scene is null || !scene.Value.isLoaded)
            {
                return GameObjectEvidence.Unknown(
                    "The snapshot's scene cannot be resolved to a loaded scene.");
            }
            var parent = GameObjectEvidence.ResolveParent(parentToken, out var isRoot);
            if (!isRoot && parent is null)
            {
                return GameObjectEvidence.Unknown(
                    "The snapshot's parent GameObject cannot be resolved.");
            }
            var live = GameObjectEvidence.LiveChildren(parent?.transform, scene.Value);

            // NotApplied: the target is still there with its recorded
            // identity and the child list is exactly the snapshot's —
            // re-execution can only delete the target, never anything else.
            if (GameObjectEvidence.SameChildList(children, live))
            {
                return new ReconcileResult.NotApplied();
            }

            // Completed: the parent's children are the snapshot minus the
            // target — and the target really cannot be resolved any more,
            // so nothing else was deleted and the target was not moved.
            var expected = new JArray();
            var removedTarget = false;
            foreach (var entry in children)
            {
                if (GameObjectEvidence.HandleOf(entry) is { } raw &&
                    raw == targetRaw.Value)
                {
                    if (removedTarget)
                    {
                        return GameObjectEvidence.Unknown(
                            "The snapshot's child list holds the target twice.");
                    }
                    removedTarget = true;
                    continue;
                }
                expected.Add(entry);
            }
            if (!removedTarget)
            {
                return GameObjectEvidence.Unknown(
                    "The snapshot's child list does not contain the target.");
            }
            if (!GameObjectEvidence.SameChildList(expected, live))
            {
                return GameObjectEvidence.Unknown(
                    "The parent's children differ from the snapshot by more " +
                    "than the target alone.");
            }
            if (SessionHandle.Resolve(targetRaw.Value) is not null ||
                (targetGlobalId is not null &&
                    GlobalObjectId.TryParse(targetGlobalId, out var parsed) &&
                    GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) is not null))
            {
                return GameObjectEvidence.Unknown(
                    "The target still resolves outside the recorded parent; " +
                    "it may have been moved rather than deleted.");
            }
            return new ReconcileResult.Completed(
                new Dictionary<string, JsonValue>(0));
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Deletes a GameObject and its hierarchy from a scene.")
            .Ja("GameObject削除",
                "対象の GameObject とその子階層をシーンから削除します。",
                "GameObject", "削除", "階層")
            .Input(TargetPort, "unity.GameObjectRef", required: true)
            .Effects(EffectNames.SceneModify)
            .Undo(UndoSupport.Full)
            .Idempotency(Idempotency.Reconcilable)
            .Build() with
            {
                DisplayName = "Delete GameObject",
                Description =
                    "Destroys the target GameObject together with its whole child " +
                    "hierarchy. The deletion is recorded through the step's Undo " +
                    "group, so a single Undo restores the object. This is a " +
                    "destructive Operation.",
                Category = "GameObject",
                Tags = new[] { "gameObject", "scene", "hierarchy" },
                Keywords = new[] { "gameobject", "delete", "destroy", "remove" },
                Destructive = true,
                Examples = new[]
                {
                    new OperationExample(
                        "Delete the GameObject created by an earlier step",
                        GameObjectOperationExamples.Delete),
                },
            };
    }
}
