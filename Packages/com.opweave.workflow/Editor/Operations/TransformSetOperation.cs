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
    /// <c>unity.transform.set</c> (SPEC §I.1): sets the position, rotation
    /// (Euler degrees) and/or local scale of a GameObject's transform — at
    /// least one of the three is required (OW-OP-006). <c>space</c> selects
    /// world or local for position and rotation (default local); scale is
    /// always local. Undo <see cref="UndoSupport.Full"/>,
    /// <see cref="Idempotency.Idempotent"/>.
    /// </summary>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class TransformSetOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.transform.set";

        internal const string TargetPort = "target";
        internal const string PositionPort = "position";
        internal const string RotationPort = "rotation";
        internal const string ScalePort = "scale";
        internal const string SpacePort = "space";

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var parameters = ctx.Parameters;
            var hasField =
                Supplied(ctx, parameters, PositionPort) ||
                Supplied(ctx, parameters, RotationPort) ||
                Supplied(ctx, parameters, ScalePort);
            var diagnostics = new List<Diagnostic>();
            if (!hasField)
            {
                diagnostics.Add(MissingField());
            }
            if (parameters.TryGetValue(SpacePort, out var space) && space.IsPresent &&
                space.Value?.ToToken() is JValue { Type: JTokenType.String } value &&
                !IsKnownSpace((string?)value.Value))
            {
                diagnostics.Add(BadSpace());
            }
            return diagnostics;
        }

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (ctx.IsPending(PositionPort) && ctx.IsPending(RotationPort) &&
                ctx.IsPending(ScalePort))
            {
                return base.Preflight(ctx);
            }
            if (!ctx.IsPresent(PositionPort) && !ctx.IsPresent(RotationPort) &&
                !ctx.IsPresent(ScalePort))
            {
                return new PreflightResult(
                    new[] { MissingField() },
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));
            }
            if (ctx.IsPresent(SpacePort) &&
                !IsKnownSpace(ctx.Get<string>(SpacePort)))
            {
                return new PreflightResult(
                    new[] { BadSpace() },
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));
            }
            try
            {
                ctx.Get<GameObject>(TargetPort);
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
            var hasPosition = ctx.IsPresent(PositionPort);
            var hasRotation = ctx.IsPresent(RotationPort);
            var hasScale = ctx.IsPresent(ScalePort);
            if (!hasPosition && !hasRotation && !hasScale)
            {
                return StepResult.Failed(MissingField());
            }
            var space = ctx.IsPresent(SpacePort)
                ? ctx.Get<string>(SpacePort)
                : "local";
            if (!IsKnownSpace(space))
            {
                return StepResult.Failed(BadSpace());
            }
            var world = string.Equals(space, "world", StringComparison.Ordinal);
            var transform = target.transform;

            // The step's §G3 Undo group needs the pre-change record so one
            // Undo restores position, rotation and scale together.
            Undo.RecordObject(transform, "Set Transform of " + target.name);
            if (hasPosition)
            {
                var position = ctx.Get<Vector3>(PositionPort);
                if (!GameObjectEvidence.IsFinite(position))
                {
                    return StepResult.Failed(NotFinite(PositionPort));
                }
                if (world)
                {
                    transform.position = position;
                }
                else
                {
                    transform.localPosition = position;
                }
            }
            if (hasRotation)
            {
                // unity.Euler is a Vector3 of degrees (§B4); world sets the
                // transform's world euler angles, local the local ones.
                var rotation = ctx.Get<Vector3>(RotationPort);
                if (!GameObjectEvidence.IsFinite(rotation))
                {
                    return StepResult.Failed(NotFinite(RotationPort));
                }
                if (world)
                {
                    transform.eulerAngles = rotation;
                }
                else
                {
                    transform.localEulerAngles = rotation;
                }
            }
            if (hasScale)
            {
                // §I.1 note: scale is always local, whatever space says.
                var scale = ctx.Get<Vector3>(ScalePort);
                if (!GameObjectEvidence.IsFinite(scale))
                {
                    return StepResult.Failed(NotFinite(ScalePort));
                }
                transform.localScale = scale;
            }

            ctx.Changes.Modified(
                new ChangeTarget(
                    "gameObject",
                    RefHierarchyPath.For(target),
                    null,
                    GlobalObjectId.GetGlobalObjectIdSlow(target).ToString()),
                undoable: true);
            return StepResult.Succeeded();
        }

        // Whether a transform field counts as supplied for static
        // validation: a written or resolved constant marks it Present; a
        // data edge or a workflow-input binding feeds it without any value,
        // so the context's static type (SPEC §0.6 SG-06 StaticTypeOf) is the
        // only signal it is fed at all.
        private static bool Supplied(
            StaticContext ctx, IReadOnlyDictionary<string, InputValue> parameters, string port) =>
            (parameters.TryGetValue(port, out var input) && input.IsPresent) ||
            ctx.StaticTypeOf(port) is not null;

        private static bool IsKnownSpace(string? space) =>
            string.Equals(space, "local", StringComparison.Ordinal) ||
            string.Equals(space, "world", StringComparison.Ordinal);

        private static Diagnostic MissingField() =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                "At least one of position, rotation or scale is required.");

        private static Diagnostic BadSpace() =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                "The space must be 'local' or 'world'.",
                port: SpacePort);

        private static Diagnostic NotFinite(string port) =>
            ErrorCodes.Create(
                ErrorCodes.OW_TYPE_003,
                "The value is not a finite Vector3.",
                port: port);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Sets a transform's position, rotation (Euler) and/or scale; scale is always local.")
            .Ja("Transform設定",
                "GameObject の位置・回転（オイラー角）・スケールを設定します。1項目以上必須。scale は常にローカルです。",
                "Transform", "位置", "回転", "スケール")
            .Input(TargetPort, "unity.GameObjectRef", required: true)
            .Input(PositionPort, "unity.Vector3", required: false)
            .Input(RotationPort, "unity.Euler", required: false)
            .Input(ScalePort, "unity.Vector3", required: false)
            .Input(SpacePort, "core.String", required: false,
                constraints: c => c.Enum("local", "world"),
                @default: JsonValue.From(new JValue("local")))
            .Effects(EffectNames.SceneModify)
            .Undo(UndoSupport.Full)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Set Transform",
                Description =
                    "Sets the target GameObject's position, rotation (Euler angles in " +
                    "degrees) and/or scale. At least one of the three inputs is " +
                    "required. The space input (default 'local') selects local or " +
                    "world for position and rotation; scale is always applied " +
                    "locally whatever space says.",
                Category = "GameObject",
                Tags = new[] { "gameObject", "transform", "scene" },
                Keywords = new[] { "transform", "position", "rotation", "scale", "move", "rotate" },
                Examples = new[]
                {
                    new OperationExample(
                        "Move the created GameObject to a position and scale it",
                        GameObjectOperationExamples.TransformSet),
                },
            };
    }
}
