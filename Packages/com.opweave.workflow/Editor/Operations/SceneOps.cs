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
    /// The shared scene-state helpers of the §I.1 Scene Operations
    /// (SPEC §G5 items 1–3): the loaded-scene enumeration every dirty-scene
    /// check and the <c>unity.scene.create</c> §F5 evidence row work from,
    /// the dirty-scene save-or-fail rule of <c>dirtyScenes</c>, the
    /// <c>{kind:"scene", path, guid}</c> output shape of a SceneRef, the
    /// asset-path → physical-path mapping and the OW-UNITY-005/OW-UNITY-011
    /// diagnostics these four Operations share.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Preview scenes (<see cref="EditorSceneManager.IsPreviewScene"/>, the
    /// in-memory contents a G6 <c>open_contents</c> or a tool opens) never
    /// count as loaded scenes here: the Editor's "at least one scene" rule
    /// and the §F5 loaded-scene evidence are about the user's scenes, which
    /// is also why <see cref="RefResolver"/> skips them when resolving.
    /// </para>
    /// <para>
    /// OW-UNITY-011 is the §J code for a scene state that prevents the
    /// operation. Its published meaning covers the unsaved-scenes refusal
    /// these Operations emit for <c>dirtyScenes: fail</c>, and this file
    /// uses the same code for the two neighbouring refusals no other §J
    /// code names: an unsavable dirty scene (<c>dirtyScenes: save</c> on a
    /// scene with no path) and the refusal to close or discard the last
    /// loaded scene — a scene-state precondition of the same kind. The
    /// choice is recorded in the implementation PR for review.
    /// </para>
    /// </remarks>
    internal static class SceneOps
    {
        /// <summary>The scene output port all four Operations share (§I.1).</summary>
        internal const string ScenePort = "scene";

        /// <summary>The shared <c>dirtyScenes</c> port (§I.1).</summary>
        internal const string DirtyScenesPort = "dirtyScenes";

        /// <summary>The shared <c>mode</c> port (§I.1).</summary>
        internal const string ModePort = "mode";

        /// <summary>The <c>dirtyScenes</c> value that refuses on unsaved scenes (§I.1 default).</summary>
        internal const string DirtyScenesFail = "fail";

        /// <summary>The <c>dirtyScenes</c> value that saves first (§I.1).</summary>
        internal const string DirtyScenesSave = "save";

        /// <summary>The <c>mode</c> value that replaces the loaded scene set (§I.1).</summary>
        internal const string ModeSingle = "single";

        /// <summary>The <c>mode</c> value that adds to it (§I.1).</summary>
        internal const string ModeAdditive = "additive";

        /// <summary>The <c>setup</c> port of <c>unity.scene.create</c> (§I.1).</summary>
        internal const string SetupPort = "setup";

        /// <summary>The <c>setup</c> value producing an empty scene (§I.1).</summary>
        internal const string SetupEmpty = "empty";

        /// <summary>The <c>setup</c> value producing the default scene (§I.1 default).</summary>
        internal const string SetupDefault = "default";

        /// <summary>The <c>path</c> input port the three path-taking Operations share (§I.1).</summary>
        internal const string PathPort = "path";

        // The roots NewSceneSetup.DefaultGameObjects creates in this
        // order — the "既定の構成" the §F5 evidence row for
        // unity.scene.create compares the produced scene against. Each
        // carries Transform plus its defining components; a scriptable
        // render pipeline adds its additional-data component, so the
        // expected component set is derived from the pipeline this Editor
        // actually runs rather than written down once.
        private static readonly string[] s_defaultRootNames =
            { "Main Camera", "Directional Light" };

        private static readonly string[] s_defaultCameraComponents =
            { "UnityEngine.Transform", "UnityEngine.Camera", "UnityEngine.AudioListener" };

        private static readonly string[] s_defaultLightComponents =
            { "UnityEngine.Transform", "UnityEngine.Light" };

        private static readonly string[] s_urpCameraComponents =
        {
            "UnityEngine.Transform", "UnityEngine.Camera", "UnityEngine.AudioListener",
            "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData",
        };

        private static readonly string[] s_urpLightComponents =
        {
            "UnityEngine.Transform", "UnityEngine.Light",
            "UnityEngine.Rendering.Universal.UniversalAdditionalLightData",
        };

        private static readonly string[] s_hdrpCameraComponents =
        {
            "UnityEngine.Transform", "UnityEngine.Camera", "UnityEngine.AudioListener",
            "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData",
        };

        private static readonly string[] s_hdrpLightComponents =
        {
            "UnityEngine.Transform", "UnityEngine.Light",
            "UnityEngine.Rendering.HighDefinition.HDAdditionalLightData",
        };

        private const string UrpPipelineType =
            "UnityEngine.Rendering.Universal.UniversalRenderPipeline";

        private const string HdrpPipelineType =
            "UnityEngine.Rendering.HighDefinition.HDRenderPipeline";

        /// <summary>
        /// Version 2 of the <c>unity.scene.create</c> expected-roots
        /// evidence, recorded as <c>expectedRootsVersion</c> in the
        /// Preflight snapshot. Version 2 is the pinned
        /// PrefabContentFingerprint v1 of each default root in its
        /// persisted-and-reloaded form — the complete serialized content
        /// proof of SPEC §F5, not a property subset — recorded from
        /// <see cref="DefaultRootFingerprints"/>; a snapshot carrying a
        /// missing or different version is evidence Reconcile cannot
        /// judge, so the verdict is Unknown.
        /// </summary>
        internal const int ExpectedRootsVersion = 2;

        // The pinned PrefabContentFingerprint v1 hash of every default
        // root, in root order, per supported Unity major.minor on the
        // built-in render pipeline — the expected complete serialized
        // content the §F5 row compares the candidate scene against.
        // Each table was recorded from the "[W2501-golden]" line the
        // ExpectedRootsV2_EqualsRealDefaultScene pin test logged on that
        // editor with the built-in pipeline; a Unity or pipeline change
        // that alters the default content is caught by that pin test.
        // §G6.1 comparability holds only within the same Unity version,
        // so every editor carries its own table and a major.minor
        // without one leaves the default content unprovable.
        private static readonly IReadOnlyDictionary<string, string[]> s_defaultRootFingerprints =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                // Unity 6000.0.85f1.
                ["6000.0"] = new[]
                {
                    "ace19d5acc1829d75e9639bebadb439710770b22c0a3a14925f387c5864807eb",
                    "a66e5dc8e7b16912f5b63c82b23d3b85df830bb843a12a009806c35dc64b3ec5",
                },
                // Unity 6000.5.11f1.
                ["6000.5"] = new[]
                {
                    "ace19d5acc1829d75e9639bebadb439710770b22c0a3a14925f387c5864807eb",
                    "52e0bbf8fc3bcbd5f3d0d690e067b9e51b6c51164fac2cf2f378f2fe67a446fe",
                },
            };

        /// <summary>
        /// The pinned PrefabContentFingerprint v1 hash per default root,
        /// in root order — or <see langword="null"/> while no table can
        /// prove the content on this Editor: the active render pipeline
        /// is not the built-in one, this Editor's Unity major.minor has
        /// no pinned table, or the pinned table is empty. Every one of
        /// those cases is <see cref="ReconcileResult.Unknown"/> at
        /// recovery, never Completed.
        /// </summary>
        internal static IReadOnlyList<string>? DefaultRootFingerprints() =>
            DefaultRootFingerprints(Application.unityVersion);

        // The table for one explicit Unity version string — the
        // testable half of the selection above, so a test can ask what
        // a major.minor outside the pinned set would return without
        // running that editor.
        internal static IReadOnlyList<string>? DefaultRootFingerprints(string unityVersion)
        {
            if (UnityEngine.Rendering.RenderPipelineManager.currentPipeline is not null ||
                !s_defaultRootFingerprints.TryGetValue(
                    MajorMinor(unityVersion), out var table) ||
                table.Length == 0)
            {
                return null;
            }
            return table;
        }

        // The major.minor prefix of a Unity version string — the
        // "6000.0" of "6000.0.85f1" — at the granularity §G6.1
        // comparability pins a fingerprint table for.
        private static string MajorMinor(string unityVersion)
        {
            var first = unityVersion.IndexOf('.');
            var second = first < 0 ? -1 : unityVersion.IndexOf('.', first + 1);
            return second < 0 ? unityVersion : unityVersion.Substring(0, second);
        }

        /// <summary>
        /// The expected root configuration of a scene created with
        /// <paramref name="setup"/>, as a JSON array — empty for
        /// <see cref="SetupEmpty"/>; for <see cref="SetupDefault"/>, one
        /// <c>{name, active, children, components, fingerprint}</c>
        /// object per root the default setup produces, in hierarchy
        /// order. <c>components</c> is the exact component type-name
        /// multiset on the root; it is absent when the active render
        /// pipeline makes the produced set unpredictable.
        /// <c>fingerprint</c> is the pinned PrefabContentFingerprint v1
        /// hash of the root's persisted content — recorded only while
        /// <see cref="DefaultRootFingerprints"/> can prove it; any other
        /// pipeline, a Unity major.minor without a pinned table, or an
        /// unfilled table leaves the default content unprovable, which
        /// Reconcile treats as Unknown.
        /// </summary>
        internal static JArray ExpectedRoots(string setup) =>
            ExpectedRoots(setup, Application.unityVersion);

        // The explicit-version half of <see cref="ExpectedRoots"/> —
        // the same expectation one unpinned editor's Preflight would
        // record, so a test can build that snapshot without running
        // that editor.
        internal static JArray ExpectedRoots(string setup, string unityVersion)
        {
            if (string.Equals(setup, SetupEmpty, StringComparison.Ordinal))
            {
                return new JArray();
            }
            var pipeline =
                UnityEngine.Rendering.RenderPipelineManager.currentPipeline;
            var pipelineName = pipeline is null ? null : pipeline.GetType().FullName;
            var fingerprints = DefaultRootFingerprints(unityVersion);
            return new JArray(
                DefaultRoot(0, ExpectedComponents(
                    pipelineName,
                    s_defaultCameraComponents, s_urpCameraComponents, s_hdrpCameraComponents),
                    FingerprintAt(fingerprints, 0)),
                DefaultRoot(1, ExpectedComponents(
                    pipelineName,
                    s_defaultLightComponents, s_urpLightComponents, s_hdrpLightComponents),
                    FingerprintAt(fingerprints, 1)));
        }

        // The pinned fingerprint of the root at <paramref name="index"/>,
        // or null when the table cannot prove this Editor's default
        // content — Preflight then records no fingerprint member, which
        // Reconcile reads as unprovable evidence.
        private static string? FingerprintAt(
            IReadOnlyList<string>? hashes, int index) =>
            hashes is not null && index < hashes.Count ? hashes[index] : null;

        // A fresh default root: active, no children, the expected
        // component set and the pinned fingerprint of its persisted
        // content when they are provable on this Editor's pipeline and
        // Unity version.
        private static JObject DefaultRoot(
            int index, string[]? components, string? fingerprint)
        {
            var root = new JObject
            {
                ["name"] = s_defaultRootNames[index],
                ["active"] = true,
                ["children"] = 0,
            };
            if (components is not null)
            {
                root["components"] = new JArray(components);
            }
            if (fingerprint is not null)
            {
                root["fingerprint"] = fingerprint;
            }
            return root;
        }

        // The component set a fresh Execute of the default setup produces
        // on this Editor's render pipeline — the built-in set, the URP or
        // HDRP set including its additional-data component, or null when
        // another pipeline runs and the produced set cannot be proved.
        private static string[]? ExpectedComponents(
            string? pipelineName, string[] builtin, string[] urp, string[] hdrp)
        {
            if (pipelineName is null)
            {
                return builtin;
            }
            if (string.Equals(pipelineName, UrpPipelineType, StringComparison.Ordinal))
            {
                return urp;
            }
            return string.Equals(pipelineName, HdrpPipelineType, StringComparison.Ordinal)
                ? hdrp
                : null;
        }

        /// <summary>Every loaded non-preview scene, in SceneManager order.</summary>
        internal static List<Scene> LoadedScenes()
        {
            var scenes = new List<Scene>(SceneManager.sceneCount);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene))
                {
                    scenes.Add(scene);
                }
            }
            return scenes;
        }

        /// <summary>Every loaded non-preview scene that has unsaved changes.</summary>
        internal static List<Scene> DirtyScenes()
        {
            var dirty = new List<Scene>();
            foreach (var scene in LoadedScenes())
            {
                if (scene.isDirty)
                {
                    dirty.Add(scene);
                }
            }
            return dirty;
        }

        /// <summary>
        /// The <c>{path, name}</c> list form of the loaded non-preview scenes —
        /// the JSON shape a <c>unity.scene.create</c> Preflight snapshot
        /// records for its §F5 "loaded scenes" evidence member. An unsaved
        /// (Untitled) scene contributes its empty path and its in-memory
        /// name — which a same-named replacement would reproduce — so it
        /// can never be re-identified across an interruption: an
        /// empty-path entry in either the recorded or the current list
        /// makes the comparison Unknown, never Completed or NotApplied.
        /// </summary>
        internal static JArray LoadedSceneList()
        {
            var list = new JArray();
            foreach (var scene in LoadedScenes())
            {
                list.Add(new JObject
                {
                    ["path"] = scene.path,
                    ["name"] = scene.name,
                });
            }
            return list;
        }

        /// <summary>
        /// Any loaded non-preview scene that has never been saved — a
        /// scene whose path is the empty string. Unity refuses an
        /// additive <c>NewScene</c> while one is loaded, so
        /// <c>unity.scene.create</c> Preflight and Execute refuse the
        /// same state deterministically with OW-UNITY-011.
        /// </summary>
        internal static bool UntitledSceneLoaded()
        {
            foreach (var scene in LoadedScenes())
            {
                if (scene.path.Length == 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The active scene as a <c>{path, name}</c> object for the same
        /// snapshot, or <see langword="null"/> when no non-preview scene is
        /// active.
        /// </summary>
        internal static JObject? ActiveSceneEntry()
        {
            var active = SceneManager.GetActiveScene();
            return active.IsValid() && !EditorSceneManager.IsPreviewScene(active)
                ? new JObject { ["path"] = active.path, ["name"] = active.name }
                : null;
        }

        /// <summary>
        /// The fully keyed SceneRef JSON of a scene asset path — the same
        /// shape <see cref="RefResolver"/> writes
        /// (<c>{kind:"scene", path, guid}</c>, SPEC §G2 "every key the object
        /// can hold").
        /// </summary>
        internal static JsonValue SceneRefOf(string path) =>
            JsonValue.From(new JObject
            {
                ["kind"] = "scene",
                ["path"] = path,
                ["guid"] = AssetDatabase.AssetPathToGUID(path),
            });

        /// <summary>
        /// The physical location of a project asset path — the same mapping
        /// <see cref="EditorRequirementEvaluator"/> applies: Packages/
        /// resolves through the owning package, anything else under the
        /// project root. <see langword="null"/> for a path that maps nowhere.
        /// </summary>
        internal static string? ToPhysicalPath(string assetPath)
        {
            if (assetPath.StartsWith(AssetPathPolicy.PackagesPrefix, StringComparison.Ordinal))
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                var prefix = info?.assetPath is { } packageRoot
                    ? packageRoot + "/"
                    : null;
                if (prefix is null ||
                    !assetPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }
                return Path.GetFullPath(Path.Combine(
                    info!.resolvedPath, assetPath.Substring(prefix.Length)));
            }
            var full = Path.GetFullPath(assetPath);
            var projectRoot =
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")) +
                Path.DirectorySeparatorChar;
            return full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }

        /// <summary>
        /// Anything occupying an asset path: a file, a directory, a leftover
        /// orphan .meta, or an asset the database still knows — the
        /// <c>unity.scene.create</c> target is never written over an existing
        /// path (§I.1 "既存パスは OW-UNITY-005"). OnlyExistingAssets asks about
        /// assets that exist; the default overload also answers for deleted
        /// ones whose mapping the database remembers.
        /// </summary>
        internal static bool Occupied(string? physical, string assetPath) =>
            (physical is not null &&
                (File.Exists(physical) ||
                 Directory.Exists(physical) ||
                 File.Exists(physical + ".meta"))) ||
            AssetDatabase.AssetPathToGUID(
                assetPath, AssetPathToGUIDOptions.OnlyExistingAssets).Length > 0;

        /// <summary>
        /// The §G5 item 1 dirty-scene rule shared by the single-mode open
        /// and the close: with <c>dirtyScenes: fail</c> any unsaved loaded
        /// scene refuses the step with OW-UNITY-011; with
        /// <c>dirtyScenes: save</c> every dirty loaded scene is saved first —
        /// which a scene with no path cannot be, so an Untitled dirty scene
        /// still refuses the step with OW-UNITY-011. Returns the refusal
        /// diagnostic, or <see langword="null"/> when the rule let the step
        /// proceed (saving what had to be saved and recording each save as a
        /// §F7 change on <paramref name="ctx"/>).
        /// </summary>
        internal static Diagnostic? ApplyDirtyScenesRule(StepContext ctx, IReadOnlyList<Scene> dirty)
        {
            if (dirty.Count == 0)
            {
                return null;
            }
            // Read through the port's resolved value rather than re-reading,
            // so a defaulted "fail" and a written "fail" behave identically.
            // A pending edge-fed port has no value yet (SPEC §D8 item 1) and
            // takes the same "fail" default.
            var mode = ctx.IsPresent(DirtyScenesPort) &&
                !ctx.IsPending(DirtyScenesPort)
                ? ctx.Get<string>(DirtyScenesPort)
                : DirtyScenesFail;
            if (!string.Equals(mode, DirtyScenesSave, StringComparison.Ordinal))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_011,
                    "Unsaved scenes prevent the operation.",
                    port: DirtyScenesPort,
                    hint: "Save the open scenes first, or set 'dirtyScenes' to 'save'.");
            }
            foreach (var scene in dirty)
            {
                if (scene.path.Length == 0 || !EditorSceneManager.SaveScene(scene))
                {
                    return ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_011,
                        "An unsaved scene has no path to be saved to.",
                        port: DirtyScenesPort,
                        hint: "Save the untitled scene under a path first.");
                }
                ctx.Changes.Saved(
                    new ChangeTarget(
                        "asset", scene.path,
                        AssetDatabase.AssetPathToGUID(scene.path)),
                    undoable: false);
            }
            return null;
        }

        /// <summary>
        /// The OW-UNITY-005 refusal every Scene Operation shares (§J "invalid
        /// path or conflicting existing asset"), on the port that carries it.
        /// </summary>
        internal static Diagnostic InvalidPath(string port, string message, string hint) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005, message, port: port, hint: hint);

        /// <summary>
        /// The OW-UNITY-011 refusal of §J / §G5 item 1 — a scene state that
        /// prevents the operation.
        /// </summary>
        internal static Diagnostic SceneStateBlocked(string message, string? port = null, string? hint = null) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_011, message, port: port, hint: hint);
    }
}
