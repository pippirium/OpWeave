#nullable enable

// T-W2.6-03 [Edit] test fixture: the Prefab assets this ticket's EditMode
// tests work on and the step seams their Operation-level
// Preflight/Execute/Reconcile calls need — a recording journal append behind
// Core's StepResourceRegistry (SPEC §E6 item 4), the §F7 change recorder and a
// UnityStepContext carrying the production prefab-contents Ref lookup.
//
// Probe Prefabs are created at runtime under Assets/OpWeavePrefabContentsProbe/
// and deleted by TearDown (the PrefabContentFingerprintTests pattern). The
// Example's declared fixture under Assets/OpWeaveExamples/ is written together
// with a hand-authored .meta pinning the guid the Example's constant
// unity.PrefabAssetRef carries, so applying the fixture makes the published
// workflow name an asset the project really holds.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal static class PrefabContentsFixture
    {
        internal const string ExampleFolder = "Assets/OpWeaveExamples";
        internal const string ExamplePath = ExampleFolder + "/Hud.prefab";
        internal const string ExampleGuid = "9f4a1c73e2b84d56a0c3b7e51d826f04";
        internal const string ProbeFolder = "Assets/OpWeavePrefabContentsProbe";

        /// <summary>
        /// The Run every harness registry of this ticket's tests binds. An
        /// acquisition is identified by its Run and step — the release handler
        /// may discard only the contents that acquisition opened — so a test that
        /// releases a resource names the same Run and the step that acquired it
        /// (<see cref="Harness.Step"/>), and a test that wants a foreign
        /// acquisition names another one.
        /// </summary>
        internal const string RunId = "run";

        // A one-GameObject Prefab on fixed fileIDs; the .meta beside it pins
        // ExampleGuid so the asset's identity is fixed rather than whatever
        // guid a runtime-created Prefab would get.
        private const string ExampleYaml =
            "%YAML 1.1\n" +
            "%TAG !u! tag:unity3d.com,2011:\n" +
            "--- !u!1 &100\n" +
            "GameObject:\n" +
            "  m_ObjectHideFlags: 0\n" +
            "  m_CorrespondingSourceObject: {fileID: 0}\n" +
            "  m_PrefabInstance: {fileID: 0}\n" +
            "  m_PrefabAsset: {fileID: 0}\n" +
            "  serializedVersion: 6\n" +
            "  m_Component:\n" +
            "  - component: {fileID: 400}\n" +
            "  m_Layer: 0\n" +
            "  m_Name: Hud\n" +
            "  m_TagString: Untagged\n" +
            "  m_Icon: {fileID: 0}\n" +
            "  m_NavMeshLayer: 0\n" +
            "  m_StaticEditorFlags: 0\n" +
            "  m_IsActive: 1\n" +
            "--- !u!4 &400\n" +
            "Transform:\n" +
            "  m_ObjectHideFlags: 0\n" +
            "  m_CorrespondingSourceObject: {fileID: 0}\n" +
            "  m_PrefabInstance: {fileID: 0}\n" +
            "  m_PrefabAsset: {fileID: 0}\n" +
            "  m_GameObject: {fileID: 100}\n" +
            "  serializedVersion: 2\n" +
            "  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}\n" +
            "  m_LocalPosition: {x: 0, y: 0, z: 0}\n" +
            "  m_LocalScale: {x: 1, y: 1, z: 1}\n" +
            "  m_ConstrainProportionsScale: 0\n" +
            "  m_Children: []\n" +
            "  m_Father: {fileID: 0}\n" +
            "  m_RootOrder: 0\n" +
            "  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n";

        private const string ExampleMeta =
            "fileFormatVersion: 2\n" +
            "guid: " + ExampleGuid + "\n" +
            "PrefabImporter:\n" +
            "  externalObjects: {}\n" +
            "  userData: \n" +
            "  assetBundleName: \n" +
            "  assetBundleVariant: \n";

        internal static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>
        /// Writes the fixture the Examples declare (path, guid and root from
        /// their <c>fixtureManifest</c>) and imports it synchronously.
        /// </summary>
        internal static void CreateExampleFixture()
        {
            EnsureFolder(ExampleFolder, "OpWeaveExamples");
            var full = Disk(ExamplePath);
            File.WriteAllText(full, ExampleYaml);
            File.WriteAllText(full + ".meta", ExampleMeta);
            AssetDatabase.ImportAsset(
                ExamplePath, ImportAssetOptions.ForceSynchronousImport);
            Assert.IsNotNull(
                AssetDatabase.LoadAssetAtPath<GameObject>(ExamplePath),
                "the Example fixture Prefab must import");
            Assert.AreEqual(ExampleGuid, AssetDatabase.AssetPathToGUID(ExamplePath),
                "the fixture's .meta must pin the guid the Example names");
        }

        /// <summary>
        /// Creates a probe Prefab at <see cref="ProbeFolder"/>/<paramref name="fileName"/>
        /// — a root named after the file carrying one child named
        /// <paramref name="childName"/> when one is given — and returns the
        /// imported asset.
        /// </summary>
        internal static GameObject CreateProbePrefab(string fileName, string? childName = null)
        {
            EnsureFolder(ProbeFolder, "OpWeavePrefabContentsProbe");
            var root = new GameObject(Path.GetFileNameWithoutExtension(fileName));
            try
            {
                if (childName is not null)
                {
                    var child = new GameObject(childName);
                    child.transform.SetParent(root.transform, worldPositionStays: false);
                }
                var path = ProbeFolder + "/" + fileName;
                var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
                Assert.IsNotNull(saved, "the probe Prefab must save: " + path);
                return saved!;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        internal static string ProbePath(string fileName) => ProbeFolder + "/" + fileName;

        internal static string GuidOf(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            Assert.IsNotEmpty(guid, "the probe asset must carry a guid: " + assetPath);
            return guid;
        }

        internal static void EnsureFolder(string assetFolder, string folderName)
        {
            if (!AssetDatabase.IsValidFolder(assetFolder))
            {
                AssetDatabase.CreateFolder("Assets", folderName);
            }
        }

        /// <summary>
        /// Deletes every probe artifact this ticket's tests create and
        /// refreshes the asset database once.
        /// </summary>
        internal static void Cleanup()
        {
            ClosePrefabMode();
            UnloadAllOpenedContents();
            foreach (var folder in new[] { ProbeFolder, ExampleFolder })
            {
                if (AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                }
            }
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Releases the contents a test that failed mid-assertion may still
        /// hold open. An Editor that kept them would leak the preview scene
        /// into every later test of the run, so the sweep runs in TearDown
        /// whatever the test asserted; the §E6 item 4 release seam is not
        /// involved (nothing is journaled for a test's own cleanup). A Prefab
        /// Mode stage is left alone: it is the user's editing session, not a
        /// test's opened contents, and unloading it is the very defect this
        /// ticket's release path refuses.
        /// </summary>
        internal static void UnloadAllOpenedContents()
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate == null || candidate.transform.parent != null)
                {
                    continue;
                }
                var scene = candidate.scene;
                if (!scene.IsValid() || !EditorSceneManager.IsPreviewScene(scene) ||
                    !scene.path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                    PrefabStageUtility.GetPrefabStage(candidate) != null)
                {
                    continue;
                }
                PrefabUtility.UnloadPrefabContents(candidate);
            }
        }

        /// <summary>
        /// Exits Prefab Mode if a test left it open, so the next test starts
        /// from the same Editor state and <see cref="Cleanup"/> can delete the
        /// asset the stage still shows. The 6000.0 public API names no way to
        /// close a stage, so the editor-internal <c>CloseAll</c> is reached
        /// reflectively when it exists and closing the stage's own scene is the
        /// fallback.
        /// </summary>
        internal static void ClosePrefabMode()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage is null)
            {
                return;
            }
            var closeAll = typeof(PrefabStageUtility).GetMethod(
                "CloseAll",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (closeAll is not null)
            {
                closeAll.Invoke(null, null);
                return;
            }
            EditorSceneManager.CloseScene(stage.scene, true);
        }

        private static string Disk(string assetPath) =>
            Path.Combine(ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>The constant <c>unity.PrefabAssetRef</c> naming an asset.</summary>
        internal static JsonValue PrefabRef(string guid, string path) =>
            JsonValue.From(new JObject
            {
                ["kind"] = "prefabAsset",
                ["guid"] = guid,
                ["path"] = path,
            });

        /// <summary>
        /// The seams one Operation-level call needs: a recording append behind
        /// the §E6 item 4 resource registry, the §F7 change recorder, and
        /// contexts built over the production prefab-contents Ref lookup.
        /// </summary>
        internal sealed class Harness
        {
            private readonly List<TrackedRunResource> _resources;
            private readonly List<JournalEntry> _records;

            internal Harness(int step = 0)
            {
                Step = step;
                Changes = new StepChangeRecorder(step, "subject");
                _resources = new List<TrackedRunResource>();
                _records = new List<JournalEntry>();
            }

            // A sibling step of the same Run: one tracked-resource list and one
            // journal seam, two step bindings. SPEC §F5's evidence is the same
            // step's resource, so a test of two open_contents steps of one
            // Prefab needs two registries over the list the Run really has
            // (§E6 item 4).
            private Harness(
                int step,
                List<TrackedRunResource> resources,
                List<JournalEntry> records)
            {
                Step = step;
                Changes = new StepChangeRecorder(step, "subject");
                _resources = resources;
                _records = records;
            }

            internal int Step { get; }
            internal StepChangeRecorder Changes { get; }

            /// <summary>Another step bound to this Run's tracked-resource list
            /// and its append seam.</summary>
            internal Harness AnotherStep(int step) =>
                new Harness(step, _resources, _records);

            /// <summary>Every <c>resource_acquired</c>/<c>resource_released</c>
            /// the Operation journaled, in order.</summary>
            internal IReadOnlyList<JournalEntry> Records => _records;

            /// <summary>The acquisitions a §F2 journal fold rebuilt — the state
            /// a new Editor session holds after a crash or restart. The entry
            /// carries the <c>step</c> of its record, which defaults to this
            /// harness's own step: §F5's Completed evidence is the acquisition
            /// of <em>the same step</em>.</summary>
            internal void SeedFoldedAcquisition(string guid, string path) =>
                SeedFoldedAcquisition(guid, path, Step);

            /// <summary>The rebuilt acquisition of one specific step.</summary>
            internal void SeedFoldedAcquisition(
                string guid, string path, int acquiredByStep) =>
                _resources.Add(new TrackedRunResource(
                    acquiredByStep,
                    PrefabContentsResources.Kind,
                    PrefabContentsResources.Data(guid, path),
                    RunResourceJournalState.Acquired));

            /// <summary>A rebuilt acquisition whose record carried no step: no
            /// step may claim it, so the state of the step's resource cannot be
            /// determined (§F5 Unknown) rather than open or not open.</summary>
            internal void SeedUnattributedFoldedAcquisition(string guid, string path) =>
                _resources.Add(new TrackedRunResource(
                    null,
                    PrefabContentsResources.Kind,
                    PrefabContentsResources.Data(guid, path),
                    RunResourceJournalState.Acquired));

            /// <summary>The live editing roots of <paramref name="assetPath"/>.
            /// Read through the adapter's own re-find so a test never re-implements
            /// the preview-scene enumeration.</summary>
            internal static IReadOnlyList<GameObject> EditingRoots(string assetPath) =>
                PrefabContentsResources.FindEditingRoots(assetPath);

            internal UnityStepContext Context(
                IReadOnlyDictionary<string, InputValue> inputs,
                IReadOnlyDictionary<string, string> inputTypes,
                IReadOnlyDictionary<string, string> outputTypes)
            {
                var registry = new StepResourceRegistry(RunId, Step, _resources, Append);
                return new UnityStepContext(
                    inputs,
                    inputTypes,
                    outputTypes,
                    RunnerHostFixture.Types,
                    new RefResolver(PrefabContentsResources.FindEditingRoot),
                    Changes,
                    new StepLogSink(
                        RunId, Step, (_, _, _, _) => Result<Unit>.Success(Unit.Value)))
                {
                    Resources = registry,
                };
            }

            /// <summary>The still-open acquisition this harness's step holds, in
            /// the form the SPEC §E6 item 4 release dispatcher is called with: the
            /// (kind, data) pair of its record plus the record's own step, which
            /// with <see cref="RunId"/> is the acquisition scope the release may
            /// unbind and unload.</summary>
            internal RunResource OpenResource(string guid, string path) =>
                new RunResource(
                    PrefabContentsResources.Kind,
                    PrefabContentsResources.Data(guid, path))
                {
                    AcquiringStep = Step,
                };

            /// <summary>The acquisition scope this harness's step binds — the same
            /// text its <see cref="StepResourceRegistry"/> reports and therefore
            /// the one an ownership binding of this harness is recorded under.
            /// Dropping the binding through it is how a test simulates the session
            /// state an Editor restart lost.</summary>
            internal string Scope => RunResourceScope.Of(RunId, Step);

            /// <summary>The context of an <c>open_contents</c> step: its
            /// required <c>prefab</c> port fed by a constant PrefabAssetRef.</summary>
            internal UnityStepContext OpenContext(string guid, string path) =>
                Context(
                    new Dictionary<string, InputValue>
                    {
                        ["prefab"] = InputValue.Present(PrefabRef(guid, path)),
                    },
                    new Dictionary<string, string> { ["prefab"] = "unity.PrefabAssetRef" },
                    new Dictionary<string, string> { ["root"] = "unity.GameObjectRef" });

            /// <summary>The context of a <c>save_contents</c> step: its required
            /// <c>root</c> port fed with the Ref an <c>open_contents</c> step
            /// published (SPEC §G2 — the same Ref shape the Runner carries over
            /// a data edge).</summary>
            internal UnityStepContext SaveContext(JsonValue rootRef) =>
                Context(
                    new Dictionary<string, InputValue>
                    {
                        ["root"] = InputValue.Present(rootRef),
                    },
                    new Dictionary<string, string> { ["root"] = "unity.GameObjectRef" },
                    new Dictionary<string, string> { ["prefab"] = "unity.PrefabAssetRef" });

            /// <summary>The Ref <c>open_contents</c> published on its
            /// <c>root</c> output after a successful Execute.</summary>
            internal static JsonValue RootRef(UnityStepContext context)
            {
                Assert.IsTrue(
                    context.Outputs.TryGetValue("root", out var root) && root is not null,
                    "open_contents must publish its root output");
                return root!;
            }

            /// <summary>The step's published <c>prefab</c> output after a
            /// successful save.</summary>
            internal static JsonValue PrefabOutput(UnityStepContext context)
            {
                Assert.IsTrue(
                    context.Outputs.TryGetValue("prefab", out var prefab) && prefab is not null,
                    "save_contents must publish its prefab output");
                return prefab!;
            }

            private Result<Unit> Append(
                JournalRecordType type, int? step, JsonValue data)
            {
                _records.Add(new JournalEntry(type, step, data));
                return Result<Unit>.Success(Unit.Value);
            }
        }

        /// <summary>One journaled Run-resource record as the step's append saw
        /// it.</summary>
        internal sealed record JournalEntry(
            JournalRecordType Type, int? Step, JsonValue Data)
        {
            /// <summary>The resource identity the record names
            /// (<c>{kind, data}</c>).</summary>
            internal string Kind => ((JObject)Data.ToToken())["kind"]!.ToString();

            internal string DataText => ((JObject)Data.ToToken())["data"]!.ToString(
                Newtonsoft.Json.Formatting.None);
        }
    }
}
