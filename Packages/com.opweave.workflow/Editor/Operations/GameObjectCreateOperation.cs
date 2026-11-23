#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.gameobject.create</c> (SPEC §I.1, §G5 item 4): creates a
    /// GameObject — optionally a primitive — in a scene under a parent.
    /// With no <c>parent</c> the object is created at the scene's root; with
    /// no <c>scene</c> either, in the active scene. Undo
    /// <see cref="UndoSupport.Full"/>, <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §F5 evidence row: Preflight records the target scene and parent
    /// (root when none), the parent's ordered children (handle · globalId ·
    /// name), the inputs and the session id. Reconcile returns
    /// <see cref="ReconcileResult.Completed"/> only when, in the same
    /// session, exactly one child that was not in the snapshot exists under
    /// the parent, every snapshot child is still there, and the new child's
    /// name, primitive composition and position match the inputs — then the
    /// <c>gameObject</c> output is rebuilt from that child. An unchanged
    /// child list is <see cref="ReconcileResult.NotApplied"/>; anything else
    /// is <see cref="ReconcileResult.Unknown"/>.
    /// </para>
    /// <para>
    /// Local, reversible choices where the SPEC leaves detail open:
    /// <c>position</c> sets <c>localPosition</c> (a root object's local
    /// position is its world position); a <c>parent</c> in a different scene
    /// than an explicit <c>scene</c> is OW-UNITY-002 (the §G2 "keys
    /// disagree" code); a <c>parent</c> supplied alone names its own scene
    /// (§I.1 precedence: parent, then scene root, then the active scene);
    /// the sibling position of the new child does not take
    /// part in the Completed check — identity and content are the evidence.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class GameObjectCreateOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.gameobject.create";

        internal const string NamePort = "name";
        internal const string ScenePort = "scene";
        internal const string ParentPort = "parent";
        internal const string PrimitivePort = "primitive";
        internal const string PositionPort = "position";
        internal const string GameObjectPort = "gameObject";

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
            // Run; the evidence row cannot be captured either.
            if (ctx.IsPending(NamePort) || ctx.IsPending(ScenePort) ||
                ctx.IsPending(ParentPort) || ctx.IsPending(PrimitivePort) ||
                ctx.IsPending(PositionPort))
            {
                return base.Preflight(ctx);
            }
            var resolved = ResolvePlacement(ctx);
            if (!resolved.Ok)
            {
                return new PreflightResult(
                    resolved.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var placement = resolved.Value!;
            var snapshot = new JObject
            {
                ["name"] = ctx.Get<string>(NamePort),
                ["primitive"] = PrimitiveOf(ctx),
                ["scene"] = GameObjectEvidence.SceneKey(placement.Scene),
                ["parent"] = placement.Parent is null
                    ? JValue.CreateNull()
                    : GameObjectEvidence.RefOf(placement.Parent),
                ["children"] = placement.Parent is null
                    ? GameObjectEvidence.ChildrenOf(placement.Scene)
                    : GameObjectEvidence.ChildrenOf(placement.Parent.transform),
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            if (ctx.IsPresent(PositionPort))
            {
                var position = ctx.Get<Vector3>(PositionPort);
                snapshot["position"] = new JObject
                {
                    ["x"] = position.x, ["y"] = position.y, ["z"] = position.z,
                };
            }
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
            var name = ctx.Get<string>(NamePort);
            var primitive = PrimitiveOf(ctx);
            if (!IsKnownPrimitive(primitive))
            {
                return StepResult.Failed(BadPrimitive(primitive));
            }
            var resolved = ResolvePlacement(ctx);
            if (!resolved.Ok)
            {
                return StepResult.Failed(resolved.Diagnostics[0]);
            }
            var placement = resolved.Value!;

            var created = Create(primitive);
            Undo.RegisterCreatedObjectUndo(created, "Create GameObject " + name);
            created.name = name;
            if (created.scene.handle != placement.Scene.handle)
            {
                SceneManager.MoveGameObjectToScene(created, placement.Scene);
            }
            if (placement.Parent is not null)
            {
                Undo.SetTransformParent(
                    created.transform, placement.Parent.transform, false,
                    "Create GameObject " + name);
            }
            if (ctx.IsPresent(PositionPort))
            {
                var position = ctx.Get<Vector3>(PositionPort);
                if (!GameObjectEvidence.IsFinite(position))
                {
                    return StepResult.Failed(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "The position is not a finite Vector3.",
                        port: PositionPort));
                }
                created.transform.localPosition = position;
            }

            ctx.Changes.Created(
                new ChangeTarget(
                    "gameObject",
                    RefHierarchyPath.For(created),
                    null,
                    GlobalObjectId.GetGlobalObjectIdSlow(created).ToString()),
                undoable: true);
            ctx.SetOutput(GameObjectPort, created);
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
            if (obj["name"] is not JValue { Type: JTokenType.String } nameToken ||
                obj["primitive"] is not JValue { Type: JTokenType.String } primitiveToken ||
                obj["scene"] is not JObject sceneKey ||
                obj["parent"] is not { } parentToken ||
                obj["children"] is not JArray children ||
                obj["editorSessionId"] is not JValue { Type: JTokenType.String } sessionToken)
            {
                return GameObjectEvidence.Unknown(
                    "The Preflight snapshot is missing a required evidence member.");
            }
            // §F5 common rule 3: the scene objects a session handle names
            // die with the session, so a different session can prove nothing.
            if (!string.Equals(
                (string)sessionToken.Value!, EditorSessionId.GetOrCreate(),
                StringComparison.Ordinal))
            {
                return GameObjectEvidence.Unknown(
                    "The snapshot belongs to a different Editor session.");
            }

            var name = (string)nameToken.Value!;
            var primitive = (string)primitiveToken.Value!;
            if (!IsKnownPrimitive(primitive))
            {
                return GameObjectEvidence.Unknown(
                    "The snapshot's primitive is not a known value.");
            }
            var expectedPosition = Vector3.zero;
            if (obj["position"] is { } positionToken)
            {
                if (positionToken is not JObject positionObj ||
                    !ReadComponent(positionObj, "x", out var x) ||
                    !ReadComponent(positionObj, "y", out var y) ||
                    !ReadComponent(positionObj, "z", out var z))
                {
                    return GameObjectEvidence.Unknown(
                        "The snapshot's position is not a readable Vector3.");
                }
                expectedPosition = new Vector3(x, y, z);
            }

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
            if (isRoot && parentToken.Type != JTokenType.Null)
            {
                return GameObjectEvidence.Unknown(
                    "The snapshot's parent member is malformed.");
            }
            var live = GameObjectEvidence.LiveChildren(parent?.transform, scene.Value);

            // NotApplied: the parent's child list is exactly the snapshot's —
            // the step provably added nothing and re-execution only creates.
            if (GameObjectEvidence.SameChildList(children, live))
            {
                return new ReconcileResult.NotApplied();
            }

            // Completed requires every snapshot child still present and
            // exactly one new child — the artifact — with matching content.
            var snapshotHandles = GameObjectEvidence.SnapshotHandles(children);
            GameObject? added = null;
            var addedCount = 0;
            var missingSnapshotChild = false;
            foreach (var entry in children)
            {
                if (GameObjectEvidence.HandleOf(entry) is { } oldRaw)
                {
                    var stillThere = false;
                    foreach (var child in live)
                    {
                        if (SessionHandle.RawOf(child) == oldRaw)
                        {
                            stillThere = true;
                            break;
                        }
                    }
                    if (!stillThere)
                    {
                        missingSnapshotChild = true;
                        break;
                    }
                }
            }
            foreach (var child in live)
            {
                if (!snapshotHandles.Contains(SessionHandle.RawOf(child)))
                {
                    added = child;
                    addedCount++;
                }
            }
            if (missingSnapshotChild || addedCount != 1 || added is null)
            {
                return GameObjectEvidence.Unknown(
                    "The parent's children differ from the snapshot by something " +
                    "other than exactly one new child.");
            }
            // Each content check names its own sub-check so a rejected
            // artifact says exactly which evidence failed (§F5 stays
            // fail-closed — every reason below ends in Unknown).
            if (!string.Equals(added.name, name, StringComparison.Ordinal))
            {
                return GameObjectEvidence.Unknown(
                    "The new child does not match the step's inputs: " +
                    "name differs.");
            }
            if (!GameObjectEvidence.ComponentsMatch(added, primitive, out var why))
            {
                return GameObjectEvidence.Unknown(
                    "The new child does not match the step's inputs: " + why + ".");
            }
            if (!GameObjectEvidence.SameVector(
                expectedPosition, added.transform.localPosition))
            {
                return GameObjectEvidence.Unknown(
                    "The new child does not match the step's inputs: " +
                    "position differs.");
            }
            return new ReconcileResult.Completed(
                new Dictionary<string, JsonValue>
                {
                    [GameObjectPort] = JsonValue.From(GameObjectEvidence.RefOf(added)),
                });
        }

        // The target scene and parent of the create: an explicit scene port
        // wins, a parent alone names its own scene (the §I.1 precedence —
        // a parent in a non-active scene must not be refused), and only
        // with neither does the active scene apply. The §G2 "keys disagree"
        // failure runs only when both ports are supplied.
        private static Result<Placement> ResolvePlacement(StepContext ctx)
        {
            Scene scene;
            GameObject? parent;
            try
            {
                parent = ctx.IsPresent(ParentPort) ? ctx.Get<GameObject>(ParentPort) : null;
                var hasScene = ctx.IsPresent(ScenePort);
                scene = hasScene
                    ? ctx.Get<Scene>(ScenePort)
                    : parent is not null
                        ? parent.scene
                        : SceneManager.GetActiveScene();
                if (hasScene && parent is not null &&
                    parent.scene.handle != scene.handle)
                {
                    return Result<Placement>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_002,
                        "The parent GameObject is not in the target scene.",
                        port: ParentPort));
                }
            }
            catch (StepContextException failure)
            {
                return Result<Placement>.Failure(failure.Diagnostics);
            }
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return Result<Placement>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_003,
                    "The target scene is not loaded.",
                    port: ScenePort));
            }
            return Result<Placement>.Success(new Placement(scene, parent));
        }

        private sealed class Placement
        {
            internal Placement(Scene scene, GameObject? parent)
            {
                Scene = scene;
                Parent = parent;
            }

            internal Scene Scene { get; }

            internal GameObject? Parent { get; }
        }

        private static GameObject Create(string primitive) => primitive switch
        {
            "cube" => GameObject.CreatePrimitive(PrimitiveType.Cube),
            "sphere" => GameObject.CreatePrimitive(PrimitiveType.Sphere),
            "capsule" => GameObject.CreatePrimitive(PrimitiveType.Capsule),
            "cylinder" => GameObject.CreatePrimitive(PrimitiveType.Cylinder),
            "plane" => GameObject.CreatePrimitive(PrimitiveType.Plane),
            "quad" => GameObject.CreatePrimitive(PrimitiveType.Quad),
            _ => new GameObject(),
        };

        private static string PrimitiveOf(StepContext ctx) =>
            ctx.IsPresent(PrimitivePort) ? ctx.Get<string>(PrimitivePort) : "none";

        private static bool IsKnownPrimitive(string primitive) =>
            GameObjectEvidence.ExpectedComponents(primitive).Length != 0;

        private static Diagnostic BadPrimitive(string primitive) =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                "The primitive must be one of none, cube, sphere, capsule, " +
                "cylinder, plane or quad.",
                port: PrimitivePort);

        private static bool ReadComponent(JObject obj, string name, out float value)
        {
            value = 0f;
            if (obj[name] is not JValue { Type: JTokenType.Float or JTokenType.Integer } token)
            {
                return false;
            }
            var component = token.ToObject<float>();
            if (float.IsNaN(component) || float.IsInfinity(component))
            {
                return false;
            }
            value = component;
            return true;
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Creates a GameObject or primitive in a scene, optionally under a parent.")
            .Ja("GameObject作成",
                "シーン内に GameObject（またはプリミティブ）を作成します。parent 省略時はルート、scene 省略時はアクティブシーンです。",
                "GameObject", "作成", "プリミティブ", "階層")
            .Input(NamePort, "core.String", required: true)
            .Input(ScenePort, "unity.SceneRef", required: false)
            .Input(ParentPort, "unity.GameObjectRef", required: false)
            .Input(PrimitivePort, "core.String", required: false,
                constraints: c => c.Enum(
                    "none", "cube", "sphere", "capsule", "cylinder", "plane", "quad"),
                @default: JsonValue.From(new JValue("none")))
            .Input(PositionPort, "unity.Vector3", required: false)
            .Output(GameObjectPort, "unity.GameObjectRef")
            .Effects(EffectNames.SceneModify)
            .Undo(UndoSupport.Full)
            .Idempotency(Idempotency.Reconcilable)
            .Requires("scene_loaded", ScenePort)
            .Build() with
            {
                DisplayName = "Create GameObject",
                Description =
                    "Creates a GameObject — or a cube, sphere, capsule, cylinder, plane or " +
                    "quad primitive — in the given scene under the given parent. With no " +
                    "parent the object is created at the scene's root; with no scene either, " +
                    "in the active scene. The optional position is the object's local " +
                    "position (its world position for a root object).",
                Category = "GameObject",
                Tags = new[] { "gameObject", "scene", "hierarchy" },
                Keywords = new[] { "gameobject", "create", "primitive", "spawn" },
                Examples = new[]
                {
                    new OperationExample(
                        "Create a cube named Player at the origin",
                        GameObjectOperationExamples.Create),
                },
            };
    }
}
