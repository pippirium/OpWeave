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
    /// <c>unity.prefab.unpack</c> (SPEC §I.2, §G6 item 7): severs the
    /// referenced Prefab instance's link to its Prefab asset through
    /// <see cref="PrefabUtility.UnpackPrefabInstance(GameObject, PrefabUnpackMode, InteractionMode)"/>
    /// with <see cref="InteractionMode.UserAction"/> — the hierarchy keeps
    /// its values, it simply stops being an instance — and republishes the
    /// instance root as a plain <c>unity.GameObjectRef</c>. Immediate, Undo
    /// <see cref="UndoSupport.Full"/>, <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>mode</c> port selects <see cref="PrefabUnpackMode.OutermostRoot"/>
    /// (<c>"outermost"</c>, the default — deeper nested Prefab instances
    /// keep their links) or <see cref="PrefabUnpackMode.Completely"/>
    /// (<c>"completely"</c> — every nested instance link goes too).
    /// </para>
    /// <para>
    /// The §F5 evidence row: Preflight records the scene key, the target's
    /// full PrefabInstanceRef (with session handle), the requested mode,
    /// the source Prefab's path and guid, the ordered depth-first
    /// structure entries of the whole hierarchy (handle · name · ordered
    /// component evidence · prefab-membership state), the instance's own
    /// override list and the Editor session id.
    /// <see cref="Reconcile"/> works only inside the recorded session: the
    /// target still an outermost instance of the recorded Prefab with the
    /// identical structure and override list is
    /// <see cref="ReconcileResult.NotApplied"/>; the same objects in the
    /// same places with the mode's expected membership state (the target's
    /// members freed, deeper instances untouched — or everything freed
    /// under "completely") is
    /// <see cref="ReconcileResult.Completed"/> with the
    /// <c>unity.GameObjectRef</c> output restored; anything else — a
    /// different session, an unresolvable target, a changed hierarchy, a
    /// partially unpacked state, a foreign modification — is
    /// <see cref="ReconcileResult.Unknown"/>.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class PrefabUnpackOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.prefab.unpack";

        internal const string InstancePort = "instance";
        internal const string ModePort = "mode";
        internal const string GameObjectPort = "gameObject";

        internal const string ModeOutermost = "outermost";
        internal const string ModeCompletely = "completely";

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
            // Run, and a missing required port yields no evidence to record.
            if (ctx.IsPending(InstancePort) || ctx.IsPending(ModePort) ||
                !ctx.IsPresent(InstancePort))
            {
                return base.Preflight(ctx);
            }
            var inputs = ReadInputs(ctx);
            if (!inputs.Ok)
            {
                return new PreflightResult(
                    inputs.Diagnostics, Array.Empty<PlannedChange>(),
                    s_emptySnapshot);
            }
            var target = inputs.Value!;

            var prefabPath =
                PrefabCreateEvidence.NormalizedInstancePath(target.Root);
            var snapshot = new JObject
            {
                ["scene"] = GameObjectEvidence.SceneKey(target.Root.scene),
                ["target"] = (JObject)new RefResolver().ToRef(
                    target.Root, "unity.PrefabInstanceRef").ToToken(),
                ["mode"] = target.Completely ? ModeCompletely : ModeOutermost,
                ["prefab"] = new JObject
                {
                    ["path"] = prefabPath,
                    ["guid"] = prefabPath.Length == 0
                        ? string.Empty
                        : AssetDatabase.AssetPathToGUID(prefabPath),
                },
                // The instance's complete shape and its own override list —
                // the before-state both verdicts compare against.
                ["structure"] = PrefabInstanceEvidence.StructureOf(target.Root),
                ["overrides"] = PrefabInstanceEvidence.OverridesOf(target.Root),
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            return new PreflightResult(
                Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                JsonValue.From(snapshot));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var inputs = ReadInputs(ctx);
            if (!inputs.Ok)
            {
                return StepResult.Failed(inputs.Diagnostics[0]);
            }
            var target = inputs.Value!;

            try
            {
                PrefabUtility.UnpackPrefabInstance(
                    target.Root,
                    target.Completely
                        ? PrefabUnpackMode.Completely
                        : PrefabUnpackMode.OutermostRoot,
                    InteractionMode.UserAction);
            }
            catch (Exception e)
            {
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_007,
                    "The Prefab instance could not be unpacked: " +
                    e.GetType().Name,
                    port: InstancePort));
            }
            ctx.Changes.Modified(
                new ChangeTarget(
                    "gameObject",
                    RefHierarchyPath.For(target.Root),
                    null,
                    GlobalObjectId.GetGlobalObjectIdSlow(target.Root).ToString()),
                undoable: true);
            ctx.SetOutput(GameObjectPort, target.Root);
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
                return Unknown("The Preflight snapshot is not an object.");
            }
            if (obj["scene"] is not JObject sceneKey ||
                obj["target"] is not JObject targetRef ||
                obj["mode"] is not JValue { Type: JTokenType.String } modeToken ||
                obj["prefab"] is not JObject prefab ||
                prefab["path"] is not JValue { Type: JTokenType.String } pathToken ||
                prefab["guid"] is not JValue { Type: JTokenType.String } guidToken ||
                obj["structure"] is not JArray structure ||
                obj["overrides"] is not { } overridesToken ||
                !Member(obj, "editorSessionId", JTokenType.String, out var sessionToken))
            {
                return Unknown(
                    "The Preflight snapshot is missing a required evidence member.");
            }
            var mode = (string)modeToken.Value!;
            if (!string.Equals(mode, ModeOutermost, StringComparison.Ordinal) &&
                !string.Equals(mode, ModeCompletely, StringComparison.Ordinal))
            {
                return Unknown(
                    "The snapshot's mode is not a known unpack mode.");
            }
            var completely = string.Equals(
                mode, ModeCompletely, StringComparison.Ordinal);
            // §F5 common rule 3: both verdicts of this row are the
            // same-session ones — the structure entries are session handles.
            if (!string.Equals(
                (string)sessionToken!, EditorSessionId.GetOrCreate(),
                StringComparison.Ordinal))
            {
                return Unknown("The snapshot belongs to a different Editor session.");
            }
            var prefabPath = (string)pathToken.Value!;
            var prefabGuid = (string)guidToken.Value!;

            var scene = GameObjectEvidence.ResolveScene(sceneKey);
            if (scene is null || !scene.Value.isLoaded)
            {
                return Unknown(
                    "The snapshot's scene cannot be resolved to a loaded scene.");
            }
            var target = GameObjectEvidence.HandleOf(targetRef) is { } raw
                ? SessionHandle.Resolve(raw) as GameObject
                : null;
            if (target is null)
            {
                return Unknown(
                    "The recorded target instance cannot be resolved in this session.");
            }
            if (target.scene.handle != scene.Value.handle)
            {
                return Unknown(
                    "The recorded target lives in a different scene than the snapshot.");
            }

            // NotApplied: the target is still an outermost instance of the
            // recorded Prefab with the identical structure and override
            // list — the step provably changed nothing and a rerun only
            // unpacks.
            if (PrefabUtility.IsOutermostPrefabInstanceRoot(target))
            {
                var livePath =
                    PrefabCreateEvidence.NormalizedInstancePath(target);
                var liveGuid = livePath.Length == 0
                    ? string.Empty
                    : AssetDatabase.AssetPathToGUID(livePath);
                if (string.Equals(livePath, prefabPath, StringComparison.Ordinal) &&
                    string.Equals(liveGuid, prefabGuid, StringComparison.Ordinal) &&
                    JToken.DeepEquals(
                        overridesToken,
                        PrefabInstanceEvidence.OverridesOf(target)) &&
                    JToken.DeepEquals(
                        structure,
                        PrefabInstanceEvidence.StructureOf(target)))
                {
                    return new ReconcileResult.NotApplied();
                }
                return Unknown(
                    "The target is still a Prefab instance but its source, " +
                    "structure or overrides differ from the snapshot.");
            }

            // Completed: the same objects in the same places with the
            // mode's expected membership state — the target's members
            // freed and deeper instances untouched, or the whole hierarchy
            // freed under "completely". Any foreign change, partial unpack
            // or misplaced object fails this check into Unknown.
            if (!PrefabInstanceEvidence.StructuresMatch(
                    structure, target, completely, prefabPath, out var reason))
            {
                return Unknown(
                    "The unpacked hierarchy does not match the snapshot: " +
                    reason + ".");
            }
            try
            {
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        [GameObjectPort] =
                            new RefResolver().ToRef(target, "unity.GameObjectRef"),
                    });
            }
            catch (ArgumentException)
            {
                return Unknown(
                    "The unpacked object could not be published as a reference.");
            }
        }

        private sealed class Target
        {
            internal GameObject Root = null!;
            internal bool Completely;
        }

        // The validated inputs: the instance resolves through
        // PrefabInstanceEvidence (its own OW-UNITY-001/004 diagnostics pass
        // through); the optional mode must be one of the §I.2 enum values —
        // anything else violates the declared constraint (OW-OP-006).
        private static Result<Target> ReadInputs(StepContext ctx)
        {
            var instance = PrefabInstanceEvidence.ResolveInstance(ctx, InstancePort);
            if (!instance.Ok)
            {
                return Result<Target>.Failure(instance.Diagnostics);
            }
            string? mode;
            try
            {
                mode = ctx.IsPresent(ModePort)
                    ? ctx.Get<string>(ModePort)
                    : ModeOutermost;
            }
            catch (StepContextException failure)
            {
                return Result<Target>.Failure(failure.Diagnostics);
            }
            if (!string.Equals(mode, ModeOutermost, StringComparison.Ordinal) &&
                !string.Equals(mode, ModeCompletely, StringComparison.Ordinal))
            {
                return Result<Target>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_006,
                    "The mode must be \"outermost\" or \"completely\".",
                    port: ModePort));
            }
            return Result<Target>.Success(new Target
            {
                Root = instance.Value!,
                Completely = string.Equals(
                    mode, ModeCompletely, StringComparison.Ordinal),
            });
        }

        private static bool Member(
            JObject obj, string name, JTokenType type, out JToken? value)
        {
            value = obj[name];
            return value is not null && value.Type == type;
        }

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Sever a Prefab instance's link to its Prefab asset, keeping its values.")
            .Ja(
                "Prefab インスタンスをアンパック",
                "Prefab インスタンスと元の Prefab アセットのリンクを解除する。値は保持され、Undo 可能。mode \"outermost\"（既定）は最外層のみ、\"completely\" は入れ子も含めて解除する。",
                "Prefab", "アンパック", "インスタンス")
            .Input(InstancePort, "unity.PrefabInstanceRef", required: true)
            .Input(ModePort, "core.String", required: false,
                constraints: c => c.Enum(ModeOutermost, ModeCompletely),
                @default: JsonValue.From(new JValue(ModeOutermost)))
            .Output(GameObjectPort, "unity.GameObjectRef")
            .Effects(EffectNames.SceneModify)
            .Undo(UndoSupport.Full)
            .Idempotency(Idempotency.Reconcilable)
            .Build() with
            {
                DisplayName = "Unpack Prefab Instance",
                Description =
                    "Severs the referenced Prefab instance's link to its " +
                    "Prefab asset through Unity's user-action " +
                    "UnpackPrefabInstance — the hierarchy keeps every value, " +
                    "it simply stops being an instance — and republishes the " +
                    "root as a plain GameObjectRef. Mode \"outermost\" (the " +
                    "default) frees only the target instance; \"completely\" " +
                    "frees every nested instance too. The change is a scene " +
                    "modification and fully Undoable; the Operation is " +
                    "Reconcilable for the same Editor session (SPEC §F5).",
                Category = "Prefab",
                Tags = new[] { "prefab", "instance", "unpack", "scene" },
                Keywords = new[] { "prefab", "unpack", "instance", "detach" },
                Examples = new[]
                {
                    new OperationExample(
                        "Instantiate a Prefab and unpack it completely",
                        PrefabOverrideOperationExamples.Unpack,
                        PrefabCreateOperationExamples.FixtureManifest),
                },
            };
    }
}
