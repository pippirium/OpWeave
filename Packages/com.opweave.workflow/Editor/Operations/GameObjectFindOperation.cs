#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.gameobject.find</c> (SPEC §I.1): finds the one GameObject
    /// matching the supplied criteria — a hierarchy <c>path</c> and/or a
    /// <c>name</c>, optionally narrowed by <c>tag</c> — inside the given
    /// scene or every loaded non-preview scene. <c>path</c> or <c>name</c>
    /// is required (OW-OP-006); anything but exactly one match is
    /// OW-UNITY-001 (none) or OW-UNITY-002 (several). Read-only and
    /// <see cref="Idempotency.Idempotent"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Local, reversible choice where the SPEC is silent: a <c>path</c>
    /// resolves through the §G2 RefResolver, so its ambiguity and
    /// not-found answers are the resolver's own OW-UNITY-001/002; a
    /// <c>name</c> or <c>tag</c> the resolved object then fails to satisfy
    /// means zero matches (OW-UNITY-001).
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class GameObjectFindOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.gameobject.find";

        internal const string ScenePort = "scene";
        internal const string PathPort = "path";
        internal const string NamePort = "name";
        internal const string TagPort = "tag";
        internal const string GameObjectPort = "gameObject";

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
            var hasPath = Supplied(ctx, parameters, PathPort);
            var hasName = Supplied(ctx, parameters, NamePort);
            return hasPath || hasName
                ? Array.Empty<Diagnostic>()
                : new[] { MissingCriterion() };
        }

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (ctx.IsPending(PathPort) && ctx.IsPending(NamePort))
            {
                return base.Preflight(ctx);
            }
            if (!ctx.IsPresent(PathPort) && !ctx.IsPresent(NamePort))
            {
                return new PreflightResult(
                    new[] { MissingCriterion() },
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject()));
            }
            if (ctx.IsPresent(ScenePort))
            {
                try
                {
                    ctx.Get<Scene>(ScenePort);
                }
                catch (StepContextException failure)
                {
                    return new PreflightResult(
                        failure.Diagnostics,
                        Array.Empty<PlannedChange>(),
                        JsonValue.From(new JObject()));
                }
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
            var hasPath = ctx.IsPresent(PathPort);
            var hasName = ctx.IsPresent(NamePort);
            if (!hasPath && !hasName)
            {
                return StepResult.Failed(MissingCriterion());
            }
            var name = hasName ? ctx.Get<string>(NamePort) : null;
            var tag = ctx.IsPresent(TagPort) ? ctx.Get<string>(TagPort) : null;
            var scene = ctx.IsPresent(ScenePort) ? ctx.Get<Scene>(ScenePort) : (Scene?)null;

            var matches = hasPath
                ? MatchByPath(ctx, name, tag, scene)
                : MatchByName(name!, tag, scene);
            if (!matches.Ok)
            {
                return StepResult.Failed(matches.Diagnostics[0]);
            }
            var found = matches.Value!;
            if (found.Count == 0)
            {
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_001,
                    "No GameObject matches the find criteria."));
            }
            if (found.Count > 1)
            {
                return StepResult.Failed(ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_002,
                    $"The find criteria match {found.Count} GameObjects, not exactly one."));
            }
            ctx.SetOutput(GameObjectPort, found[0]);
            return StepResult.Succeeded();
        }

        // A path criterion resolves through the §G2 resolver — its own
        // OW-UNITY-001/002 answers for a missing or ambiguous segment. An
        // explicit scene scopes the walk to that scene's roots directly: a
        // SceneRef can only name a scene by guid or path, so an unsaved
        // scene has no key the resolver's scene scope could resolve — and
        // falling back to a scene-less walk would let a same-path object in
        // another scene shadow or break the match.
        private static Result<List<GameObject>> MatchByPath(
            StepContext ctx, string? name, string? tag, Scene? scene)
        {
            var path = ctx.Get<string>(PathPort);
            if (!RefHierarchyPath.TryParse(path, out var segments))
            {
                return Result<List<GameObject>>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "The path is not a hierarchy path ('/Root/Child').",
                    port: PathPort));
            }
            Result<GameObject> resolved;
            if (scene is { } given)
            {
                if (!given.IsValid() || !given.isLoaded)
                {
                    return Result<List<GameObject>>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_003,
                        "The target scene is not loaded.",
                        port: ScenePort));
                }
                resolved = WalkPath(given.GetRootGameObjects(), segments);
            }
            else
            {
                var reference = new JObject
                {
                    ["kind"] = UnityShapeTypes.GameObjectKind,
                    ["path"] = path,
                };
                resolved = new RefResolver().Resolve<GameObject>(
                    JsonValue.From(reference));
            }
            if (!resolved.Ok)
            {
                return Result<List<GameObject>>.Failure(resolved.Diagnostics);
            }
            var candidate = resolved.Value!;
            if (!Satisfies(candidate, name, tag, scene))
            {
                return Result<List<GameObject>>.Success(new List<GameObject>());
            }
            return Result<List<GameObject>>.Success(
                new List<GameObject> { candidate });
        }

        // The §G2 hierarchy walk inside one scene's roots — the same
        // OW-UNITY-001 (missing) / OW-UNITY-002 (ambiguous) answers the
        // resolver's own walk produces, so a scoped path search judges
        // identical paths in other scenes as irrelevant.
        private static Result<GameObject> WalkPath(
            IReadOnlyList<GameObject> roots, RefHierarchyPath.Segment[] segments)
        {
            var level = roots;
            GameObject? node = null;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var sameName = new List<GameObject>();
                for (var j = 0; j < level.Count; j++)
                {
                    if (level[j].name == segment.Name)
                    {
                        sameName.Add(level[j]);
                    }
                }
                if (segment.Index >= 0)
                {
                    if (segment.Index >= sameName.Count)
                    {
                        return Result<GameObject>.Failure(ErrorCodes.Create(
                            ErrorCodes.OW_UNITY_001,
                            $"Hierarchy path segment '{segment.Name}[{segment.Index}]' matches no object.",
                            port: PathPort));
                    }
                    node = sameName[segment.Index];
                }
                else if (sameName.Count == 0)
                {
                    return Result<GameObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_001,
                        $"Hierarchy path segment '{segment.Name}' matches no object.",
                        port: PathPort));
                }
                else if (sameName.Count > 1)
                {
                    return Result<GameObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_002,
                        $"Hierarchy path segment '{segment.Name}' is ambiguous: " +
                            $"{sameName.Count} siblings share the name.",
                        port: PathPort));
                }
                else
                {
                    node = sameName[0];
                }
                var transform = node!.transform;
                var children = new List<GameObject>(transform.childCount);
                for (var j = 0; j < transform.childCount; j++)
                {
                    children.Add(transform.GetChild(j).gameObject);
                }
                level = children;
            }
            return Result<GameObject>.Success(node!);
        }

        // A name criterion searches every descendant of the scope's roots —
        // the given scene's, or every loaded non-preview scene's (the §G2
        // scope a scene-less path search uses).
        private static Result<List<GameObject>> MatchByName(
            string name, string? tag, Scene? scene)
        {
            var roots = new List<GameObject>();
            if (scene is { } given)
            {
                if (!given.IsValid() || !given.isLoaded)
                {
                    return Result<List<GameObject>>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_003,
                        "The target scene is not loaded.",
                        port: ScenePort));
                }
                roots.AddRange(given.GetRootGameObjects());
            }
            else
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var loaded = SceneManager.GetSceneAt(i);
                    if (loaded.isLoaded && !EditorSceneManager.IsPreviewScene(loaded))
                    {
                        roots.AddRange(loaded.GetRootGameObjects());
                    }
                }
            }
            var matches = new List<GameObject>();
            foreach (var root in roots)
            {
                Collect(root.transform, name, tag, scene, matches);
            }
            return Result<List<GameObject>>.Success(matches);
        }

        private static void Collect(
            Transform node, string name, string? tag, Scene? scene,
            List<GameObject> matches)
        {
            if (Satisfies(node.gameObject, name, tag, scene))
            {
                matches.Add(node.gameObject);
            }
            for (var i = 0; i < node.childCount; i++)
            {
                Collect(node.GetChild(i), name, tag, scene, matches);
            }
        }

        private static bool Satisfies(
            GameObject candidate, string? name, string? tag, Scene? scene)
        {
            if (scene is { } given && candidate.scene.handle != given.handle)
            {
                return false;
            }
            if (name is not null &&
                !string.Equals(candidate.name, name, StringComparison.Ordinal))
            {
                return false;
            }
            if (tag is not null &&
                !string.Equals(candidate.tag, tag, StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }

        // Whether the criterion port is supplied for static validation: a
        // written or resolved constant marks it Present; a data edge or a
        // workflow-input binding feeds it without any value, so the
        // context's static type (SPEC §0.6 SG-06 StaticTypeOf) is the only
        // signal it is fed at all.
        private static bool Supplied(
            StaticContext ctx, IReadOnlyDictionary<string, InputValue> parameters, string port) =>
            (parameters.TryGetValue(port, out var input) && input.IsPresent) ||
            ctx.StaticTypeOf(port) is not null;

        private static Diagnostic MissingCriterion() =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                "The path or the name input is required; supply at least one.");

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Finds the single GameObject matching a hierarchy path and/or name, optionally by tag.")
            .Ja("GameObject検索",
                "階層パス・名前（および任意のタグ）に一致する GameObject を1件だけ探します。",
                "GameObject", "検索", "名前", "タグ")
            .Input(ScenePort, "unity.SceneRef", required: false)
            .Input(PathPort, "unity.HierarchyPath", required: false)
            .Input(NamePort, "core.String", required: false)
            .Input(TagPort, "core.String", required: false)
            .Output(GameObjectPort, "unity.GameObjectRef")
            .Effects(EffectNames.Read)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Idempotent)
            .Build() with
            {
                DisplayName = "Find GameObject",
                Description =
                    "Finds the GameObject matching the supplied criteria inside the given " +
                    "scene, or every loaded scene when none is given: a hierarchy path " +
                    "('/Root/Child', '[n]' selecting among same-name siblings) and/or a " +
                    "name, optionally narrowed by a tag. The path or the name input is " +
                    "required. Zero matches fail with OW-UNITY-001 and more than one " +
                    "with OW-UNITY-002.",
                Category = "GameObject",
                Tags = new[] { "gameObject", "scene", "hierarchy" },
                Keywords = new[] { "gameobject", "find", "search", "name", "tag", "path" },
                Examples = new[]
                {
                    new OperationExample(
                        "Find the GameObject named Player",
                        GameObjectOperationExamples.Find),
                },
            };
    }
}
