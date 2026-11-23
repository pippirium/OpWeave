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
    /// <c>unity.component.remove</c> (SPEC §I.1, §G5 item 6): destroys the
    /// target component on its owning GameObject. Destructive, Undo
    /// <see cref="UndoSupport.Full"/>, <see cref="Idempotency.Reconcilable"/>.
    /// A <see cref="Transform"/> can never be removed (OW-UNITY-007), and a
    /// removal that leaves the component in place because another component
    /// depends on it through <see cref="RequireComponentAttribute"/> is
    /// OW-UNITY-007 as well — the component's survival after the removal
    /// is the failure's proof (§G5 item 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §F5 evidence row: Preflight records the owner's GameObjectRef,
    /// the target's type · handle · index, the owning GameObject's ordered
    /// component list and the session id. Reconcile in the same session
    /// returns <see cref="ReconcileResult.Completed"/> only when the
    /// target's session handle resolves to nothing and the live component
    /// list equals the snapshot minus the target — <see cref="ReconcileResult.NotApplied"/>
    /// when the live list equals the snapshot exactly (the target survives
    /// with its recorded identity, so re-execution can only delete the
    /// target itself) — and <see cref="ReconcileResult.Unknown"/> otherwise:
    /// another component added or removed, the target still resolvable
    /// (moved rather than destroyed), a different session.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class ComponentRemoveOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.component.remove";

        internal const string ComponentPort = "component";

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
            if (ctx.IsPending(ComponentPort))
            {
                return base.Preflight(ctx);
            }
            Component target;
            try
            {
                target = ctx.Get<Component>(ComponentPort);
            }
            catch (StepContextException failure)
            {
                return new PreflightResult(
                    failure.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var owner = target.gameObject;
            var components = ComponentEvidence.LiveComponents(owner);
            var snapshot = new JObject
            {
                ["scene"] = GameObjectEvidence.SceneKey(owner.scene),
                ["owner"] = GameObjectEvidence.RefOf(owner),
                ["target"] = new JObject
                {
                    ["type"] = UnityClassRelationProvider.ClassIdOf(target.GetType()),
                    ["handle"] = SessionHandle.Write(target),
                    ["index"] = Array.IndexOf(components, target),
                },
                ["components"] = ComponentEvidence.ComponentsOf(owner),
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
            Component target;
            try
            {
                target = ctx.Get<Component>(ComponentPort);
            }
            catch (StepContextException failure)
            {
                return StepResult.Failed(failure.Diagnostics[0]);
            }
            // §I.1 note: a Transform can never be removed.
            if (target is Transform)
            {
                return StepResult.Failed(CannotRemove(
                    "The target is a Transform, which cannot be removed."));
            }
            var owner = target.gameObject;
            var blocker = RequiredByOther(owner, target);
            if (blocker is not null)
            {
                return StepResult.Failed(CannotRemove(
                    $"The component is required by {blocker.GetType().Name} on the same GameObject."));
            }
            var path = RefHierarchyPath.For(owner);
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();

            Undo.DestroyObjectImmediate(target);

            // §G5 item 6: a component that survives the removal — a
            // RequireComponent dependency the static check missed is the
            // remaining cause — is the OW-UNITY-007 the row prescribes.
            if (target != null)
            {
                return StepResult.Failed(CannotRemove(
                    "The component is still present after the removal attempt."));
            }
            ctx.Changes.Deleted(
                new ChangeTarget("component", path, null, globalId),
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
                return ComponentEvidence.Unknown("The Preflight snapshot is not an object.");
            }
            if (obj["scene"] is not JObject sceneKey ||
                obj["owner"] is not JObject ownerRef ||
                obj["target"] is not JObject targetEntry ||
                obj["components"] is not JArray components ||
                obj["editorSessionId"] is not JValue { Type: JTokenType.String } sessionToken)
            {
                return ComponentEvidence.Unknown(
                    "The Preflight snapshot is missing a required evidence member.");
            }
            if (!string.Equals(
                (string)sessionToken.Value!, EditorSessionId.GetOrCreate(),
                StringComparison.Ordinal))
            {
                return ComponentEvidence.Unknown(
                    "The snapshot belongs to a different Editor session.");
            }
            var targetHandle = ComponentEvidence.HandleOf(targetEntry);
            if (targetHandle is null)
            {
                return ComponentEvidence.Unknown(
                    "The snapshot's target carries no session handle.");
            }

            var scene = GameObjectEvidence.ResolveScene(sceneKey);
            if (scene is null || !scene.Value.isLoaded)
            {
                return ComponentEvidence.Unknown(
                    "The snapshot's scene cannot be resolved to a loaded scene.");
            }
            var owner = new RefResolver().Resolve<GameObject>(JsonValue.From(ownerRef));
            if (!owner.Ok || owner.Value == null)
            {
                return ComponentEvidence.Unknown(
                    "The snapshot's owning GameObject cannot be resolved.");
            }
            var live = ComponentEvidence.LiveComponents(owner.Value);

            // NotApplied: the component list is exactly the snapshot's —
            // the target survives with its recorded identity, and
            // re-execution can only delete the target itself.
            if (ComponentEvidence.SameComponentList(components, live))
            {
                return new ReconcileResult.NotApplied();
            }

            // Completed: the live list is the snapshot's minus the target —
            // and the target's session handle resolves to nothing, so the
            // component was destroyed rather than moved.
            var expected = new JArray();
            var removedTarget = false;
            foreach (var entry in components)
            {
                if (ComponentEvidence.HandleOf(entry) is { } raw &&
                    raw == targetHandle.Value)
                {
                    if (removedTarget)
                    {
                        return ComponentEvidence.Unknown(
                            "The snapshot's component list holds the target twice.");
                    }
                    removedTarget = true;
                    continue;
                }
                expected.Add(entry);
            }
            if (!removedTarget)
            {
                return ComponentEvidence.Unknown(
                    "The snapshot's component list does not contain the target.");
            }
            if (!ComponentEvidence.SameComponentList(expected, live))
            {
                return ComponentEvidence.Unknown(
                    "The owner's components differ from the snapshot by more " +
                    "than the target alone.");
            }
            if (SessionHandle.Resolve(targetHandle.Value) != null)
            {
                return ComponentEvidence.Unknown(
                    "The target still resolves; it may have been moved rather " +
                    "than deleted.");
            }
            return new ReconcileResult.Completed(
                new Dictionary<string, JsonValue>(0));
        }

        // The first component on the owner whose type's RequireComponent
        // attributes name the target's type (or a base of it) — Unity
        // refuses to remove a component another component depends on.
        private static Component? RequiredByOther(GameObject owner, Component target)
        {
            var targetType = target.GetType();
            foreach (var other in owner.GetComponents<Component>())
            {
                if (other == null || ReferenceEquals(other, target))
                {
                    continue;
                }
                var attributes = other.GetType()
                    .GetCustomAttributes(typeof(RequireComponent), inherit: true);
                foreach (RequireComponent attribute in attributes)
                {
                    foreach (var required in new[]
                        { attribute.m_Type0, attribute.m_Type1, attribute.m_Type2 })
                    {
                        if (required is not null && required.IsAssignableFrom(targetType))
                        {
                            return other;
                        }
                    }
                }
            }
            return null;
        }

        private static Diagnostic CannotRemove(string why) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_007,
                "The component cannot be removed: " + why,
                port: ComponentPort);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Removes a component from its GameObject; a Transform is never removable.")
            .Ja("Component削除",
                "GameObject から対象の Component を削除します。Transform は削除できません。",
                "Component", "削除", "GameObject")
            .Input(ComponentPort, "unity.ComponentRef<unity.Component>", required: true)
            .Effects(EffectNames.SceneModify)
            .Undo(UndoSupport.Full)
            .Idempotency(Idempotency.Reconcilable)
            .Build() with
            {
                DisplayName = "Remove Component",
                Description =
                    "Destroys the target component on its owning GameObject. A " +
                    "Transform cannot be removed, and a removal blocked because " +
                    "another component depends on it through RequireComponent " +
                    "fails with OW-UNITY-007. This is a destructive Operation.",
                Category = "Component",
                Tags = new[] { "component", "gameObject", "scene" },
                Keywords = new[] { "component", "remove", "delete", "detach", "gameobject" },
                Destructive = true,
                Examples = new[]
                {
                    new OperationExample(
                        "Remove the Animator added by an earlier step",
                        ComponentOperationExamples.Remove),
                },
            };
    }
}
