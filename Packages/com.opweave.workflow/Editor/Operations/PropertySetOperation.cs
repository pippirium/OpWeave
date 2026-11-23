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
    /// <c>unity.property.set</c> (SPEC §I.1, §G5 item 7, §C4): writes a JSON
    /// value into one serialized property of the Ref-addressed target
    /// through <see cref="SerializedObject.FindProperty"/>, over the §G5
    /// item 7 supported set shared with <c>unity.property.get</c>
    /// (<see cref="SerializedPropertyValues"/>). A path that names no
    /// serialized property, one deeper than three segments, an unsupported
    /// kind or a value the property cannot take is OW-UNITY-007 and nothing
    /// is committed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Effects and Undo (SPEC §I.1).</b> The actual side effect follows
    /// the target's static type — the §C4 <see cref="IEffectResolver"/>
    /// contract: a Ref type naming an asset
    /// (<c>unity.AssetRef&lt;T&gt;</c>, <c>unity.PrefabAssetRef</c>, and an
    /// <c>asset</c>/<c>prefabAsset</c> constant's <c>kind</c>) resolves to
    /// <c>asset.modify</c>; a scene-object Ref type
    /// (<c>unity.GameObjectRef</c>, <c>unity.PrefabInstanceRef</c>,
    /// <c>unity.ComponentRef&lt;T&gt;</c>) resolves to
    /// <c>scene.modify</c>; a statically unknown target reports both
    /// declared effects rather than guessing. At run time
    /// <see cref="EditorUtility.IsPersistent"/> makes the same split: a
    /// scene change is written through
    /// <see cref="SerializedObject.ApplyModifiedProperties()"/>, the §G3
    /// Undo path that gives the declared "Scene Full" one-step revert,
    /// while an asset change is applied without Undo (the declared "Asset
    /// None") and saved immediately through
    /// <see cref="AssetDatabase.SaveAssetIfDirty(UnityEngine.Object)"/> per
    /// §G5 item 7. The Descriptor's single <c>Undo</c> field carries
    /// <see cref="UndoSupport.Partial"/> — the honest middle of the
    /// per-target "Full / None" pair.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class PropertySetOperation : OperationBase, IEffectResolver
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.property.set";

        internal const string TargetPort = "target";
        internal const string PropertyPathPort = "propertyPath";
        internal const string ValuePort = "value";

        private static readonly IReadOnlyList<string> s_sceneModify =
            new[] { EffectNames.SceneModify };
        private static readonly IReadOnlyList<string> s_assetModify =
            new[] { EffectNames.AssetModify };
        private static readonly IReadOnlyList<string> s_bothEffects =
            new[] { EffectNames.SceneModify, EffectNames.AssetModify };

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        /// <remarks>
        /// SPEC §C4: the resolved effect follows the target's static type
        /// from <see cref="StaticContext.StaticTypeOf"/> — a data-edge-fed
        /// port answers with its supplier's output type — and, for a
        /// constant Ref whose static type is only the port's
        /// <c>unity.ObjectRef</c>, the Ref JSON's <c>kind</c> discriminator.
        /// A statically unknown or unrefined target can be either kind of
        /// object, so the resolver answers both declared effects rather
        /// than guessing one (a superset the §D6 positional checks then
        /// evaluate conservatively).
        /// </remarks>
        public IReadOnlyList<string> ResolveEffects(StaticContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            switch (context.StaticTypeOf(TargetPort)?.Name)
            {
                case "unity.AssetRef":
                case "unity.PrefabAssetRef":
                    return s_assetModify;
                case "unity.GameObjectRef":
                case "unity.PrefabInstanceRef":
                case "unity.ComponentRef":
                    return s_sceneModify;
            }
            if (context.Parameters.TryGetValue(TargetPort, out var input) &&
                input.IsPresent &&
                input.Value?.ToToken() is JObject target)
            {
                switch ((string?)target["kind"])
                {
                    case UnityShapeTypes.AssetKind:
                    case UnityShapeTypes.PrefabAssetKind:
                        return s_assetModify;
                    case UnityShapeTypes.GameObjectKind:
                    case UnityShapeTypes.PrefabInstanceKind:
                    case UnityShapeTypes.ComponentKind:
                        return s_sceneModify;
                }
            }
            return s_bothEffects;
        }

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
                if (failure is null)
                {
                    failure = GuardedTarget(ctx);
                }
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
            var value = ctx.Get<JsonValue>(ValuePort);
            var failure = Check(target, path, out var serialized, out var property);
            if (failure is not null)
            {
                return StepResult.Failed(failure);
            }
            serialized!.Update();
            var error = SerializedPropertyValues.Apply(
                property!, value.ToToken(), new RefResolver(),
                PropertyGetOperation.PathDepth(path), PropertyPathPort);
            if (error is not null)
            {
                // Nothing is committed: the modified properties are only
                // applied below, so the target is untouched.
                return StepResult.Failed(error);
            }

            // SPEC §D0.3 item 4: re-checked immediately before the
            // persistent write commits — a Run admitted since Preflight
            // still blocks it, and nothing is applied when it refuses.
            var guarded = GuardedTarget(target);
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }
            if (EditorUtility.IsPersistent(target))
            {
                // §I.1: an asset change is Undo None and is saved
                // immediately (§G5 item 7); the F7 record carries the
                // modify and the save separately.
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var assetPath = AssetDatabase.GetAssetPath(target);
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                ctx.Changes.Modified(
                    new ChangeTarget("asset", assetPath, guid), undoable: false);
                AssetDatabase.SaveAssetIfDirty(target);
                ctx.Changes.Saved(
                    new ChangeTarget("asset", assetPath, guid), undoable: false);
                return StepResult.Succeeded();
            }

            // §I.1: a scene change is Undo Full — ApplyModifiedProperties
            // is the §G3 Undo API that both marks the scene dirty and puts
            // the write into the step's single Undo group.
            serialized.ApplyModifiedProperties();
            var owner = target is Component component ? component.gameObject : target as GameObject;
            if (owner is not null)
            {
                ctx.Changes.Modified(
                    new ChangeTarget(
                        "gameObject",
                        RefHierarchyPath.For(owner),
                        null,
                        GlobalObjectId.GetGlobalObjectIdSlow(target).ToString()),
                    undoable: true);
            }
            return StepResult.Succeeded();
        }

        // The SPEC §D0.3 judgment of the persistent target (T-W4.2-06): a
        // scene object changes no file; an asset's file may be
        // definition-bearing. An .asmdef/.asmref member edit can rename or
        // repoint an assembly — the new value is unknowable here, so the
        // write is refused while any Run is unfinished (OW-RUN-013); every
        // other target is classified by its asset path.
        private static Diagnostic? GuardedTarget(StepContext ctx)
        {
            try
            {
                return GuardedTarget(ctx.Get<UnityEngine.Object>(TargetPort));
            }
            catch (StepContextException)
            {
                return null; // the port read already produced its own finding.
            }
        }

        private static Diagnostic? GuardedTarget(UnityEngine.Object? target)
        {
            if (target is null || !EditorUtility.IsPersistent(target))
            {
                return null;
            }
            var assetPath = AssetDatabase.GetAssetPath(target);
            if (assetPath.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                assetPath.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase))
            {
                return RunDefinitionGuard.CheckUnprovableDefinitionChange(assetPath);
            }
            return RunDefinitionGuard.CheckPath(
                assetPath, GuardedPathRole.Source);
        }

        // The precondition chain shared by Preflight and Execute: the
        // target resolves and the path is inside the depth bound and names
        // a real serialized property.
        private static Diagnostic? Check(StepContext ctx)
        {
            try
            {
                var target = ctx.Get<UnityEngine.Object>(TargetPort);
                var path = ctx.IsPresent(PropertyPathPort)
                    ? ctx.Get<string>(PropertyPathPort)
                    : null;
                return path is null
                    ? null
                    : Check(target, path, out _, out _);
            }
            catch (StepContextException error)
            {
                return error.Diagnostics[0];
            }
        }

        private static Diagnostic? Check(
            UnityEngine.Object target,
            string path,
            out SerializedObject? serialized,
            out SerializedProperty? property)
        {
            serialized = null;
            property = null;
            if (string.IsNullOrEmpty(path))
            {
                return PathNotFound(path);
            }
            if (PropertyGetOperation.PathDepth(path) > SerializedPropertyValues.MaxDepth)
            {
                return PropertyGetOperation.TooDeep(path);
            }
            serialized = new SerializedObject(target);
            property = serialized.FindProperty(path);
            return property is null ? PathNotFound(path) : null;
        }

        private static Diagnostic PathNotFound(string path) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_007,
                $"The property path '{path}' names no serialized property on the target.",
                port: PropertyPathPort);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Writes a JSON value into a serialized property of the target.")
            .Ja("プロパティ設定",
                "対象オブジェクトのシリアライズプロパティにJSON値を書き込みます。アセットは直ちに保存されます。",
                "プロパティ", "設定", "書き込み")
            .Input(TargetPort, "unity.ObjectRef", required: true)
            .Input(PropertyPathPort, "core.String", required: true)
            .Input(ValuePort, "core.Json", required: true)
            .Effects(EffectNames.SceneModify, EffectNames.AssetModify)
            // SPEC §I.1: "Scene は Full、Asset は None" — the target's kind
            // picks the Undo behaviour, and Partial is the single
            // declaration that says so honestly.
            .Undo(UndoSupport.Partial)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Set Property",
                Description =
                    "Writes the JSON 'value' into the serialized property at " +
                    "'propertyPath' of the Ref-addressed target through " +
                    "SerializedObject.FindProperty: scalars, an enum by member name, " +
                    "colors, vectors, quaternions, object references as Ref JSON, " +
                    "arrays and nested members up to a depth of three. The actual " +
                    "effect follows the target's static type — scene.modify for a " +
                    "scene object (Undo Full), asset.modify for an asset (Undo " +
                    "None, saved immediately). A missing, deeper, unsupported or " +
                    "unapplyable path is OW-UNITY-007 and writes nothing.",
                Category = "GameObject",
                Tags = new[] { "property", "write" },
                Keywords = new[] { "property", "set", "write", "serialized" },
                Examples = new[]
                {
                    new OperationExample(
                        "Rename the created GameObject",
                        PropertyOperationExamples.PropertySet),
                },
            };
    }
}
