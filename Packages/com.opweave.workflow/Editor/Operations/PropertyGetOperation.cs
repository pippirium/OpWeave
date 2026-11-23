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
    /// <c>unity.property.get</c> (SPEC §I.1, §G5 item 7): reads one
    /// serialized property of the Ref-addressed target through
    /// <see cref="SerializedObject.FindProperty"/> and returns it as the
    /// JSON the same contract writes — the §G5 item 7 set of Integer,
    /// Boolean, Float, String, Enum (by member name), Color, Vector2/3/4,
    /// Quaternion, ObjectReference (Ref JSON), arrays and Generic members,
    /// shared with <c>unity.property.set</c> through
    /// <see cref="SerializedPropertyValues"/>. A path that names no
    /// serialized property, one deeper than three segments, or a kind
    /// outside the supported set is OW-UNITY-007.
    /// <see cref="EffectNames.Read"/>, Immediate, Undo None,
    /// <see cref="Idempotency.Idempotent"/>.
    /// </summary>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class PropertyGetOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.property.get";

        internal const string TargetPort = "target";
        internal const string PropertyPathPort = "propertyPath";
        internal const string ValuePort = "value";

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (ctx.IsPending(TargetPort) || ctx.IsPending(PropertyPathPort) ||
                !ctx.IsPresent(TargetPort))
            {
                return base.Preflight(ctx);
            }
            try
            {
                var failure = Check(ctx);
                return failure is null
                    ? base.Preflight(ctx)
                    : new PreflightResult(
                        new[] { failure },
                        Array.Empty<PlannedChange>(),
                        JsonValue.From(new JObject()));
            }
            catch (StepContextException error)
            {
                return new PreflightResult(
                    error.Diagnostics,
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));
            }
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var target = ctx.Get<UnityEngine.Object>(TargetPort);
            var path = ctx.Get<string>(PropertyPathPort);
            var failure = Check(target, path, out var property);
            if (failure is not null)
            {
                return StepResult.Failed(failure);
            }
            var read = SerializedPropertyValues.Read(
                property!, new RefResolver(), PathDepth(path), PropertyPathPort);
            if (!read.Ok)
            {
                return StepResult.Failed(read.Diagnostics[0]);
            }
            ctx.SetOutput(ValuePort, JsonValue.From(read.Value!));
            return StepResult.Succeeded();
        }

        // The precondition chain shared by Preflight and Execute: the target
        // resolves, the path is inside the depth bound and names a real
        // serialized property.
        private static Diagnostic? Check(StepContext ctx)
        {
            try
            {
                var target = ctx.Get<UnityEngine.Object>(TargetPort);
                var path = ctx.IsPresent(PropertyPathPort)
                    ? ctx.Get<string>(PropertyPathPort)
                    : null;
                return path is null ? null : Check(target, path, out _);
            }
            catch (StepContextException error)
            {
                return error.Diagnostics[0];
            }
        }

        private static Diagnostic? Check(
            UnityEngine.Object target, string path, out SerializedProperty? property)
        {
            property = null;
            if (string.IsNullOrEmpty(path))
            {
                return PathNotFound(path);
            }
            if (PathDepth(path) > SerializedPropertyValues.MaxDepth)
            {
                return TooDeep(path);
            }
            var serialized = new SerializedObject(target);
            property = serialized.FindProperty(path);
            return property is null ? PathNotFound(path) : null;
        }

        // SPEC §G5 item 7's depth 3 counted as the property path's segments:
        // "nested.inner.value" is three, "numbers.Array.data[0]" is three —
        // the array's own path elements are levels like any other.
        internal static int PathDepth(string path) =>
            string.IsNullOrEmpty(path) ? 0 : path.Split('.').Length;

        private static Diagnostic PathNotFound(string path) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_007,
                $"The property path '{path}' names no serialized property on the target.",
                port: PropertyPathPort);

        internal static Diagnostic TooDeep(string path) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_007,
                $"The property path '{path}' is deeper than 3 levels.",
                port: PropertyPathPort,
                hint: "Address the member inside the depth bound instead.");

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Reads a serialized property of the target as JSON.")
            .Ja("プロパティ取得",
                "対象オブジェクトのシリアライズプロパティを読み取り、JSON値として返します。",
                "プロパティ", "取得", "読み取り")
            .Input(TargetPort, "unity.ObjectRef", required: true)
            .Input(PropertyPathPort, "core.String", required: true)
            .Output(ValuePort, "core.Json")
            .Effects(EffectNames.Read)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Get Property",
                Description =
                    "Reads the serialized property at 'propertyPath' of the Ref-addressed " +
                    "target through SerializedObject.FindProperty and returns it as JSON: " +
                    "scalars, an enum by member name, colors, vectors, quaternions, object " +
                    "references as Ref JSON, arrays and nested members up to a depth of " +
                    "three. A missing, deeper or unsupported path is OW-UNITY-007.",
                Category = "GameObject",
                Tags = new[] { "property", "read" },
                Keywords = new[] { "property", "get", "read", "serialized" },
                Examples = new[]
                {
                    new OperationExample(
                        "Read the created GameObject's name",
                        PropertyOperationExamples.PropertyGet),
                },
            };
    }
}
