#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.scene.create</c> (SPEC §I.1, §G5 item 8, §F5): creates a new
    /// scene asset at <c>path</c> — empty or with the default root setup —
    /// and loads it, additively (default) or replacing the loaded scenes in
    /// single mode. Immediate, Undo None,
    /// <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The path is a write: <see cref="AssetPathPolicy.CheckWritePath"/>
    /// refuses a malformed spelling (OW-PERM-002) and a read-only package
    /// destination (OW-UNITY-010), the §G1 <c>path_writable</c> requirement
    /// judges the allowed-paths half, and a path that is already occupied —
    /// file, directory, orphan .meta or known asset — is OW-UNITY-005
    /// (§I.1 note), never overwritten.
    /// </para>
    /// <para>
    /// Single mode shares <c>unity.scene.open</c>'s no-discard rule: the
    /// catalog gives <c>create</c> no <c>dirtyScenes</c> port, so replacing
    /// the loaded scenes while any of them is dirty is refused outright
    /// with OW-UNITY-011 rather than silently dropping unsaved work — a
    /// local, conservative choice where SPEC is silent.
    /// </para>
    /// <para>
    /// <b>Reconciliation (§F5 evidence row).</b> Preflight persists: the
    /// normalized path and its nothing-there state, the mode and setup
    /// inputs, the expected root configuration the setup produces — each
    /// expected root's name, active flag, child count, component
    /// type-name set and, while it can be pinned on this Editor, the
    /// PrefabContentFingerprint v1 hash of its complete persisted
    /// content — the <c>expectedRootsVersion</c> marker that versions
    /// that expectation, the loaded scene list and the active scene,
    /// plus the Editor session id as restart evidence only.
    /// <see cref="Reconcile"/> decides only from that evidence and never
    /// changes the Editor — recovery observes, it does not create,
    /// close or activate scenes. Nothing at the path with the loaded
    /// list unchanged is <see cref="ReconcileResult.NotApplied"/>; a
    /// scene asset at the path whose roots match the recorded
    /// expectation and that is loaded the way the mode prescribes —
    /// single mode: the only loaded scene; additive: the recorded set
    /// plus the created scene, which
    /// <see cref="EditorSceneManager.NewScene"/> leaves as the active
    /// scene, so any other active scene is not the uninterrupted
    /// result — is <see cref="ReconcileResult.Completed"/> with the
    /// SceneRef output restored; everything else — a pre-existing
    /// artifact, a mismatched or non-scene or unimported file, a scene
    /// not loaded per the mode, a changed loaded set, an active scene
    /// that is not the created one, a modified root or unprovable
    /// expected content, unrestorable evidence — is
    /// <see cref="ReconcileResult.Unknown"/>. An unsaved (untitled)
    /// scene records an empty path and can never be re-identified
    /// across an interruption, so an empty-path entry in the recorded
    /// or the current loaded set is always Unknown; additive Preflight
    /// and Execute already refuse that state with OW-UNITY-011 because
    /// Unity itself cannot create a scene additively while an untitled
    /// scene is unsaved. The artifact is a file, re-identifiable across
    /// an Editor restart by its permanent path/guid/content keys (§F5
    /// common rule 3), so the recorded session id never gates the
    /// verdict.
    /// </para>
    /// <para>
    /// For the <c>default</c> setup the root check proves the complete
    /// content, not a property subset: after names, states, child
    /// counts and component type-name sets match, each root's
    /// PrefabContentFingerprint v1 (SPEC §G6.1) — every serialized
    /// property of every component, not a hand-picked few — must equal
    /// the pinned hash recorded in the snapshot for the built-in
    /// pipeline on the pinned Unity version. A created scene that is
    /// still dirty fails closed too: unsaved in-memory changes are
    /// content the row cannot prove against the saved artifact. A
    /// missing or unknown <c>expectedRootsVersion</c>, a pipeline or
    /// Unity version the expectation was never pinned for, an unfilled
    /// pinned table, a dirty created scene, a fingerprint the walker
    /// cannot compute or any fingerprint mismatch is
    /// <see cref="ReconcileResult.Unknown"/> — the fail-closed answer
    /// §F5 demands; the <c>empty</c> setup needs no content proof
    /// because zero roots is already decidable.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class CreateSceneOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.scene.create";

        private const string SceneExtension = ".unity";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        private static readonly JsonValue s_defaultSetup =
            JsonValue.From(new JValue(SceneOps.SetupDefault));

        private static readonly JsonValue s_defaultMode =
            JsonValue.From(new JValue(SceneOps.ModeAdditive));

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // §D8 item 1: a pending port has no value to inspect in a Dry Run,
            // and a missing required port yields no evidence to record.
            if (!ctx.IsPresent(SceneOps.PathPort) || ctx.IsPending(SceneOps.PathPort))
            {
                return base.Preflight(ctx);
            }
            var failure = Check(ctx, out var single);
            if (failure is not null)
            {
                return new PreflightResult(
                    new[] { failure }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }

            var setup = Setup(ctx);
            var snapshot = new JObject
            {
                [SceneOps.PathPort] =
                    AssetPathPolicy.Normalize(ctx.Get<string>(SceneOps.PathPort)),
                ["pathExisted"] = false,
                [SceneOps.ModePort] = single ? SceneOps.ModeSingle : SceneOps.ModeAdditive,
                [SceneOps.SetupPort] = setup,
                ["expectedRoots"] = SceneOps.ExpectedRoots(setup),
                ["expectedRootsVersion"] = SceneOps.ExpectedRootsVersion,
                ["loadedScenes"] = SceneOps.LoadedSceneList(),
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            var active = SceneOps.ActiveSceneEntry();
            if (active is not null)
            {
                snapshot["activeScene"] = active;
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
            var failure = Check(ctx, out var single);
            if (failure is not null)
            {
                return StepResult.Failed(failure);
            }
            var path = AssetPathPolicy.Normalize(ctx.Get<string>(SceneOps.PathPort))!;
            if (single && SceneOps.DirtyScenes().Count > 0)
            {
                return StepResult.Failed(SceneOps.SceneStateBlocked(
                    "Unsaved scenes prevent the operation.",
                    hint: "Save the open scenes first, or create the scene additively."));
            }

            var scene = EditorSceneManager.NewScene(
                string.Equals(Setup(ctx), SceneOps.SetupEmpty, StringComparison.Ordinal)
                    ? NewSceneSetup.EmptyScene
                    : NewSceneSetup.DefaultGameObjects,
                single ? NewSceneMode.Single : NewSceneMode.Additive);
            if (!EditorSceneManager.SaveScene(scene, path))
            {
                return StepResult.Failed(SceneOps.InvalidPath(
                    SceneOps.PathPort,
                    "The new scene could not be saved at the path.",
                    "Choose a path inside an existing writable folder."));
            }

            // §I.1 declares Undo None, so the recorded change is not undoable.
            ctx.Changes.Created(
                new ChangeTarget(
                    "asset", path,
                    AssetDatabase.AssetPathToGUID(path)),
                undoable: false);
            ctx.SetOutput(SceneOps.ScenePort, SceneOps.SceneRefOf(path));
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
            if (!Member(obj, SceneOps.PathPort, JTokenType.String, out var pathToken) ||
                !Member(obj, "pathExisted", JTokenType.Boolean, out var existedToken) ||
                !Member(obj, SceneOps.ModePort, JTokenType.String, out var modeToken) ||
                !Member(obj, SceneOps.SetupPort, JTokenType.String, out var setupToken) ||
                !Member(obj, "expectedRoots", JTokenType.Array, out var rootsToken) ||
                !Member(obj, "expectedRootsVersion", JTokenType.Integer, out var versionToken) ||
                !Member(obj, "loadedScenes", JTokenType.Array, out var loadedToken) ||
                !Member(obj, "editorSessionId", JTokenType.String, out _))
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var path = (string)pathToken!;
            var pathExisted = (bool)existedToken!;
            var single = string.Equals(
                (string?)modeToken, SceneOps.ModeSingle, StringComparison.Ordinal);
            var setupIsDefault = string.Equals(
                (string?)setupToken, SceneOps.SetupDefault, StringComparison.Ordinal);
            if ((int)versionToken! != SceneOps.ExpectedRootsVersion)
            {
                return Unknown(
                    "The snapshot's expectedRootsVersion is not supported by this version.");
            }
            var expectedRoots = new List<JObject>();
            foreach (var entry in (JArray)rootsToken!)
            {
                if (entry is not JObject root ||
                    !Member(root, "name", JTokenType.String, out _) ||
                    !Member(root, "active", JTokenType.Boolean, out _) ||
                    !Member(root, "children", JTokenType.Integer, out _))
                {
                    return Unknown("The snapshot's expected root list is malformed.");
                }
                expectedRoots.Add(root);
            }

            var physical = SceneOps.ToPhysicalPath(path);
            var fileExists = physical is not null && File.Exists(physical);
            var dirExists = physical is not null && Directory.Exists(physical);
            var metaExists = physical is not null && File.Exists(physical + ".meta");
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);

            if (!fileExists && !dirExists && !metaExists && guid.Length == 0)
            {
                // Nothing occupies the target: the step provably did not
                // apply — but only while the rest of the loaded set is
                // still the snapshot's (§F5 NotApplied condition); a changed
                // scene set leaves the step's true footprint unprovable,
                // and an untitled entry — an empty path — can never be
                // re-identified at all.
                var currentLoaded = SceneOps.LoadedSceneList();
                if (ContainsUntitled((JArray)loadedToken!) ||
                    ContainsUntitled(currentLoaded))
                {
                    return Unknown(
                        "An unsaved untitled scene cannot be re-identified in the loaded scene set.");
                }
                return JToken.DeepEquals((JToken)loadedToken!, currentLoaded)
                    ? new ReconcileResult.NotApplied()
                    : (ReconcileResult)Unknown(
                        "The path is empty but the loaded scene set differs from the snapshot.");
            }
            if (pathExisted)
            {
                return Unknown(
                    "A pre-existing artifact occupies the target path; it cannot be attributed to the step.");
            }
            if (dirExists)
            {
                return Unknown("A directory occupies the target path.");
            }
            if (!fileExists)
            {
                return Unknown(metaExists
                    ? "A leftover .meta file without a file occupies the target path."
                    : "An asset database entry without a file occupies the target path.");
            }

            // A file at the path: it must be an imported scene asset — a
            // foreign file is never this step's artifact.
            if (guid.Length == 0 ||
                AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
            {
                return Unknown("The file at the target path is not an imported scene asset.");
            }
            var scene = FindLoadedScene(path);
            if (!scene.isLoaded)
            {
                return Unknown("The scene asset exists but is not loaded as the mode requires.");
            }
            if (single)
            {
                if (SceneOps.LoadedScenes().Count != 1)
                {
                    return Unknown("The single-mode scene is loaded beside other scenes.");
                }
            }
            else if (LoadedSetMismatch((JArray)loadedToken!, scene) is
                     { } setFailure)
            {
                return Unknown(setFailure);
            }
            else if (!ActiveSceneIsCreated(scene))
            {
                return Unknown("The active scene is not the created scene.");
            }

            var roots = scene.GetRootGameObjects();
            if (roots.Length != expectedRoots.Count)
            {
                return Unknown("The scene's root count does not match the expected setup.");
            }
            for (var i = 0; i < roots.Length; i++)
            {
                if (expectedRoots[i]["components"] is not JArray components)
                {
                    return Unknown(
                        "The expected root component set could not be established for the setup.");
                }
                if (!RootMatches(roots[i], expectedRoots[i]))
                {
                    return Unknown(
                        "A scene root's name, state or children do not match the expected setup.");
                }
                if (!ComponentSetMatches(roots[i], components))
                {
                    return Unknown(
                        "A scene root's component set does not match the expected setup.");
                }
            }

            if (setupIsDefault)
            {
                // The created scene must be exactly what was saved: an
                // unsaved in-memory change is content the row cannot
                // prove, and each root's complete persisted content —
                // its PrefabContentFingerprint v1 — must equal the hash
                // the snapshot recorded from the pinned table (§G6.1,
                // §F5). Observation only; nothing is changed here.
                if (scene.isDirty)
                {
                    return Unknown(
                        "The created scene carries unsaved changes; its default content cannot be proven.");
                }
                var pinned = SceneOps.DefaultRootFingerprints();
                if (pinned is null)
                {
                    return Unknown(
                        "The expected default scene content is not pinned for this pipeline and Unity version.");
                }
                for (var i = 0; i < roots.Length; i++)
                {
                    if (!Member(
                            expectedRoots[i], "fingerprint", JTokenType.String,
                            out var expectedHash) ||
                        i >= pinned.Count ||
                        !string.Equals(
                            (string?)expectedHash, pinned[i], StringComparison.Ordinal))
                    {
                        return Unknown(
                            "The expected default scene content was not recorded for the Preflight pipeline and Unity version.");
                    }
                    var computed = PrefabContentFingerprint.Compute(roots[i]);
                    if (computed is PrefabFingerprintResult.Unknown uncomputable)
                    {
                        return Unknown(
                            "A scene root's content could not be fingerprinted: " +
                            uncomputable.Reason);
                    }
                    if (!string.Equals(
                        ((PrefabFingerprintResult.Computed)computed).Fingerprint.Hash,
                        (string)expectedHash!, StringComparison.Ordinal))
                    {
                        return Unknown(
                            "A scene root's content fingerprint does not match the expected default content.");
                    }
                }
            }

            return new ReconcileResult.Completed(
                new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                {
                    [SceneOps.ScenePort] = SceneOps.SceneRefOf(path),
                });
        }

        // The additive-mode §F5 row: the loaded scenes must be exactly the
        // snapshot's recorded set plus the created scene — a closed or
        // added foreign scene is a different state from an uninterrupted
        // execution. An untitled entry carries only an empty path and an
        // in-memory name a replacement would reproduce, so a set
        // containing one can never be re-identified — Unknown on either
        // side. Returns the failure reason, or null when the set matches.
        private static string? LoadedSetMismatch(
            JArray recorded, Scene scene)
        {
            var current = SceneOps.LoadedSceneList();
            if (ContainsUntitled(recorded) || ContainsUntitled(current))
            {
                return "An unsaved untitled scene cannot be re-identified in the loaded scene set.";
            }
            if (current.Count != recorded.Count + 1)
            {
                return "The loaded scene set is not the snapshot's set plus the created scene.";
            }
            var created = new JObject
            {
                ["path"] = scene.path,
                ["name"] = scene.name,
            };
            var seenCreated = false;
            foreach (var entry in current)
            {
                if (JToken.DeepEquals(entry, created))
                {
                    if (seenCreated)
                    {
                        return "The created scene is loaded more than once.";
                    }
                    seenCreated = true;
                    continue;
                }
                var known = false;
                foreach (var previous in recorded)
                {
                    if (JToken.DeepEquals(entry, previous))
                    {
                        known = true;
                        break;
                    }
                }
                if (!known)
                {
                    return "The loaded scene set is not the snapshot's set plus the created scene.";
                }
            }
            return seenCreated
                ? null
                : "The created scene is missing from the loaded scene set.";
        }

        // A list entry records an unsaved (Untitled) scene when its path
        // is the empty string — the marker that makes the set's
        // re-identification impossible after an interruption.
        private static bool ContainsUntitled(JArray scenes)
        {
            foreach (var entry in scenes)
            {
                if (entry is JObject obj &&
                    obj["path"] is { } path &&
                    path.Type == JTokenType.String &&
                    ((string)path)!.Length == 0)
                {
                    return true;
                }
            }
            return false;
        }

        // The created scene must be found among the loaded non-preview
        // scenes by its permanent path — the key that survives a domain
        // reload, unlike a session-scoped handle lookup
        // (§F5 common rule 3).
        private static Scene FindLoadedScene(string path)
        {
            foreach (var scene in SceneOps.LoadedScenes())
            {
                if (string.Equals(scene.path, path, StringComparison.Ordinal))
                {
                    return scene;
                }
            }
            return default;
        }

        // An uninterrupted additive Execute leaves the created scene
        // active — EditorSceneManager.NewScene makes the new scene the
        // active one — so the §F5 completed state has the created scene
        // active; every other active scene is a different state.
        private static bool ActiveSceneIsCreated(Scene scene)
        {
            var active = SceneManager.GetActiveScene();
            return active.IsValid() &&
                !EditorSceneManager.IsPreviewScene(active) &&
                string.Equals(active.path, scene.path, StringComparison.Ordinal);
        }

        // A root matches the recorded expectation when its name, active
        // flag and child count match — roots carry no children in the
        // setups this Operation produces.
        private static bool RootMatches(GameObject root, JObject expected) =>
            string.Equals(root.name, (string?)expected["name"], StringComparison.Ordinal) &&
            root.activeSelf == (bool)expected["active"]! &&
            root.transform.childCount == (int)expected["children"]!;

        private static bool ComponentSetMatches(GameObject root, JArray expected)
        {
            if (expected.Count != root.GetComponents<Component>().Length)
            {
                return false;
            }
            var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var component in root.GetComponents<Component>())
            {
                if (component is null)
                {
                    return false;
                }
                var typeName = component.GetType().FullName ?? component.GetType().Name;
                remaining[typeName] =
                    remaining.TryGetValue(typeName, out var count) ? count + 1 : 1;
            }
            foreach (var entry in expected)
            {
                if (entry.Type != JTokenType.String ||
                    !remaining.TryGetValue((string)entry!, out var left) ||
                    left == 0)
                {
                    return false;
                }
                remaining[(string)entry!] = left - 1;
            }
            return true;
        }

        // A pending edge-fed setup has no value to inspect on the Dry-Run
        // path (SPEC §D8 item 1): it takes the declared default like a
        // missing port.
        private static string Setup(StepContext ctx) =>
            ctx.IsPresent(SceneOps.SetupPort) &&
                !ctx.IsPending(SceneOps.SetupPort)
                ? ctx.Get<string>(SceneOps.SetupPort)
                : SceneOps.SetupDefault;

        // The precondition chain of Preflight and Execute: the write-path
        // policy, the .unity suffix, the free target and the mode's
        // scene-state refusals — single mode's dirty-scene refusal and
        // additive mode's untitled-scene refusal (Unity cannot create a
        // scene additively while an untitled scene is unsaved, so the
        // step fails deterministically rather than throwing inside
        // NewScene).
        private static Diagnostic? Check(StepContext ctx, out bool single)
        {
            var write = AssetPathPolicy.CheckWritePath(
                ctx.Get<string>(SceneOps.PathPort), SceneOps.PathPort);
            if (!write.Ok)
            {
                single = false;
                return write.Diagnostics[0];
            }
            var path = write.Value!;
            if (!path.EndsWith(SceneExtension, StringComparison.OrdinalIgnoreCase))
            {
                single = false;
                return SceneOps.InvalidPath(
                    SceneOps.PathPort,
                    "The scene path must end with '.unity'.",
                    "Choose a path ending in '.unity' inside the allowed paths.");
            }
            var physical = SceneOps.ToPhysicalPath(path);
            if (physical is null || SceneOps.Occupied(physical, path))
            {
                single = false;
                return SceneOps.InvalidPath(
                    SceneOps.PathPort,
                    "The target path is already occupied and is never overwritten.",
                    "Choose a scene path that does not exist yet.");
            }
            single = string.Equals(
                ctx.IsPresent(SceneOps.ModePort) &&
                    !ctx.IsPending(SceneOps.ModePort)
                    ? ctx.Get<string>(SceneOps.ModePort)
                    : SceneOps.ModeAdditive,
                SceneOps.ModeSingle, StringComparison.Ordinal);
            if (single && SceneOps.DirtyScenes().Count > 0)
            {
                return SceneOps.SceneStateBlocked(
                    "Unsaved scenes prevent the operation.",
                    hint: "Save the open scenes first, or create the scene additively.");
            }
            if (!single && SceneOps.UntitledSceneLoaded())
            {
                return SceneOps.SceneStateBlocked(
                    "An unsaved untitled scene is loaded; a scene cannot be created additively.",
                    hint: "Save or close the untitled scene first, or create the scene in single mode.");
            }
            return null;
        }

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        private static bool Member(
            JObject obj, string name, JTokenType type, out JToken? value)
        {
            value = obj[name];
            return value is not null && value.Type == type;
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Creates a new scene asset and loads it, empty or with the default root setup.")
            .Ja("シーン作成",
                "新しいシーンアセットを作成して読み込みます。空または既定の構成を選べます。",
                "シーン", "作成", "新規")
            .Input(SceneOps.PathPort, "unity.AssetPath", required: true)
            .Input(SceneOps.SetupPort, "core.String", required: false,
                c => c.Enum(SceneOps.SetupEmpty, SceneOps.SetupDefault),
                @default: s_defaultSetup)
            .Input(SceneOps.ModePort, "core.String", required: false,
                c => c.Enum(SceneOps.ModeSingle, SceneOps.ModeAdditive),
                @default: s_defaultMode)
            .Output(SceneOps.ScenePort, "unity.SceneRef")
            .Effects(EffectNames.AssetCreate, EffectNames.SceneLoad)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Reconcilable)
            // SPEC §G1 item 4: the allowed-paths and writability half of the
            // write path, judged with the settings snapshot the Run's
            // acceptance validated (§H4.1, decision D-R6).
            .Requires("path_writable", SceneOps.PathPort)
            .Build() with
            {
                DisplayName = "Create Scene",
                Description =
                    "Creates a new scene at the given project asset path — empty, or with " +
                    "the default root setup — and loads it additively (the default) or as " +
                    "the only loaded scene in single mode. The path must end in '.unity'; " +
                    "an occupied path fails with OW-UNITY-005 and is never overwritten. " +
                    "Single mode while unsaved scenes are open fails with OW-UNITY-011 — " +
                    "unsaved work is never discarded — and additive mode while an unsaved " +
                    "untitled scene is loaded fails the same way. The Operation is " +
                    "Reconcilable: after an interruption it completes only when a new scene " +
                    "asset at the path has the setup's expected root configuration — for " +
                    "the default setup, including each root's pinned PrefabContentFingerprint " +
                    "v1 recorded at Preflight — and is loaded the way the mode prescribes: the created " +
                    "scene loaded per the mode and, in additive mode, the recorded set plus " +
                    "the created, still-active scene (SPEC §F5). An untitled entry in the " +
                    "loaded set is never provable and yields Unknown.",
                Category = "Scene",
                Tags = new[] { "scene", "create" },
                Keywords = new[] { "scene", "create", "new" },
                Examples = new[]
                {
                    new OperationExample(
                        "Create and open a new scene",
                        SceneOperationExamples.CreateScene),
                },
            };
    }
}
