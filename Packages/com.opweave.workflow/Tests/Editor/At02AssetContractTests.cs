#nullable enable

// T-W4.5-03 [Edit] tests: local:T-W4.5-03:at02-asset — the SPEC §T.3 AT-02
// contract applied to every standard Operation of the SPEC §I.3 Asset
// catalog group through the OpWeave.Testing §M.6 item 2 assertions:
//
//   * AssertDescriptorValid — the §C3 registration checks pass with no
//     OW-OP-003 violation (SPEC §C1 test 1);
//   * AssertIdempotency — per the declaration: an Idempotent Operation
//     (import, create_folder, find, save_all) runs twice on the same
//     inputs with an identical world and equal outputs; a Reconcilable
//     Operation (create_material, create_scriptable_object, move, copy,
//     delete) is judged through RunInterrupted at both §F5 crash windows,
//     Unknown is accepted as the safe verdict, an evidence-less Completed
//     fails, and the damaged-evidence probe admits Unknown only;
//   * AssertUndo — no §I.3 Operation declares Undo=Full, so the check is
//     exercised and skips per its own semantics;
//   * AssertDeclaredEffectsOnly — no recorded or observed change outside
//     the declared §C4 vocabulary.
//
// Every input is a deterministic fixture this file builds: probe text
// assets, a probe Shader and the test assembly's ScriptableObject types
// inside the fixture harness's own folder — a second harness refuses to
// open its scene beside unsaved changes, so the base scene stays saved.
// The asset Operations that read the active Run's frozen allowed-paths
// snapshot (move, delete, find) get the harness's settings stand-in
// through HarnessOperationSource.Override — the same stand-in the
// harness's own requirement evaluator makes for path_writable (SPEC
// §H4.1) — still constructing the standard implementation class through
// its own test-seam constructor. unity.asset.import is the group's
// Waitable: its contract runs settle through OperationTestHarness's
// RunToCompletion pump, which drives Poll on the persisted resume token
// the way the Run Loop does.
//
// The Runner half of §F5 — a Run ending at a bare step_started whose
// evidence no longer holds must end NeedsReview, never an automatic
// success or re-execution — lives in At02AssetRecoveryTests. This file
// closes with the deliberately weakened Reconcile fixture the ticket
// requires the contract to catch.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Operations;
using OpWeave.Testing;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.5-03:at02-asset</c>: the AT-02 contract suite for the
    /// SPEC §I.3 Asset group.
    /// </summary>
    internal sealed class At02AssetContractTests
    {
        // The SPEC §I.3 catalog group, verbatim and in table order — the
        // complete exercised list the AT-02 pass condition requires with
        // none skipped.
        private static readonly string[] s_specGroup =
        {
            ImportAssetOperation.OperationId,
            CreateFolderOperation.OperationId,
            CreateMaterialOperation.OperationId,
            CreateScriptableObjectOperation.OperationId,
            FindAssetsOperation.OperationId,
            MoveAssetOperation.OperationId,
            CopyAssetOperation.OperationId,
            DeleteAssetOperation.OperationId,
            SaveAllOperation.OperationId,
        };

        private const string ProbeFolder = "Assets/OpWeaveAt02Asset";
        private const string BaseScenePath = ProbeFolder + "/Base.unity";
        private const string ProbeShaderPath = ProbeFolder + "/Probe.shader";

        // A real Shader asset declaring the §G7 item 4 property kinds —
        // the same probe AssetCreateOperationTests uses, so create_material
        // applies a declared property the way the positive fixture does.
        private const string ProbeShaderName = "OpWeave/AssetCreateProbe";
        private const string ProbeShaderSource =
            "Shader \"OpWeave/AssetCreateProbe\"\n" +
            "{\n" +
            "    Properties\n" +
            "    {\n" +
            "        _Glossiness (\"Smoothness\", Range(0, 1)) = 0.5\n" +
            "        _Metallic (\"Metallic\", Range(0, 1)) = 0\n" +
            "        _Color (\"Color\", Color) = (1, 1, 1, 1)\n" +
            "        _EmissionColor (\"Emission\", Color) = (0, 0, 0, 0)\n" +
            "        _Offset (\"Offset\", Vector) = (0, 0, 0, 0)\n" +
            "        _MainTex (\"Main Tex\", 2D) = \"white\" {}\n" +
            "    }\n" +
            "    SubShader\n" +
            "    {\n" +
            "        Pass\n" +
            "        {\n" +
            "            CGPROGRAM\n" +
            "            #pragma vertex vert\n" +
            "            #pragma fragment frag\n" +
            "            float4 vert(float4 v : POSITION) : SV_Position { return v; }\n" +
            "            fixed4 frag() : SV_Target { return fixed4(1, 1, 1, 1); }\n" +
            "            ENDCG\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        private readonly List<OperationTestHarness> _harnesses =
            new List<OperationTestHarness>();
        private readonly string _tag =
            Guid.NewGuid().ToString("N").Substring(0, 6);
        private OperationTestHarness? _fixture;
        private int _serial;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveAt02Asset");
            }
            // A saved, clean base scene: an untitled caller scene would be
            // taken over by the first harness that opens — and additive
            // fixture scenes cannot be created while one is open.
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(
                EditorSceneManager.SaveScene(scene, BaseScenePath), Is.True,
                "the test's base scene must save.");
            // The §I.3 Operations that read OpWeaveRunnerHost.Current's
            // ActiveRunAllowedPaths — the frozen settings snapshot a real
            // Run's acceptance validated (SPEC §H4.1) — have no Run in a
            // workflow-less harness run, so the source override substitutes
            // the settings stand-in the harness's own requirement evaluator
            // makes, through each Operation's internal test-seam
            // constructor. The Operation under contract stays the standard
            // implementation class; discovery and eligibility still apply.
            HarnessOperationSource.ClearOverrides();
            HarnessOperationSource.Override(
                MoveAssetOperation.OperationId, 1,
                () => new MoveAssetOperation(HarnessAllowedPaths));
            HarnessOperationSource.Override(
                DeleteAssetOperation.OperationId, 1,
                () => new DeleteAssetOperation(HarnessAllowedPaths));
            HarnessOperationSource.Override(
                FindAssetsOperation.OperationId, 1,
                () => new FindAssetsOperation(
                    AssetDatabase.FindAssets,
                    AssetDatabase.GUIDToAssetPath,
                    HarnessAllowedPaths));
        }

        [TearDown]
        public void TearDown()
        {
            HarnessOperationSource.ClearOverrides();
            _fixture = null;
            foreach (var harness in _harnesses)
            {
                harness.Dispose();
            }
            _harnesses.Clear();
            // One fresh untitled scene — every loaded fixture, opened or
            // created scene is discarded before the probe folders leave.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            var probeDisk = PhysicalPath(ProbeFolder);
            if (Directory.Exists(probeDisk))
            {
                try { Directory.Delete(probeDisk, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            if (File.Exists(probeDisk + ".meta"))
            {
                try { File.Delete(probeDisk + ".meta"); } catch (Exception) { }
            }
            if (AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"))
            {
                AssetDatabase.DeleteAsset("Assets/OpWeaveTesting");
            }
            AssetDatabase.Refresh();
        }

        // ── The SPEC §I.3 group, exercised in catalog order ─────────────

        // The pass condition's coverage half: the exercised list is exactly
        // the SPEC §I.3 catalog group — every entry resolves to a real
        // standard Operation and carries a valid §C3 descriptor, so a
        // missing or skipped entry fails here before the per-Operation
        // assertions below are even reached.
        [Test]
        public void ExercisedList_IsTheSpecI3Group()
        {
            Assert.That(s_specGroup, Has.Length.EqualTo(9));
            Assert.That(s_specGroup.Distinct().Count(), Is.EqualTo(9));
            foreach (var id in s_specGroup)
            {
                Assert.DoesNotThrow(
                    () => OperationContract.AssertDescriptorValid(id), id);
            }
        }

        // unity.asset.import — the group's Waitable: Execute returns
        // Waiting with an AssetImportedCondition token and the pump drives
        // Poll to the imported-asset outcome. Declared Idempotent: the
        // second run re-imports nothing new.
        [Test]
        public void Import_Contract()
        {
            var id = ImportAssetOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, ImportInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, ImportInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, ImportInputs()));
        }

        [Test]
        public void CreateFolder_Contract()
        {
            var id = CreateFolderOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, CreateFolderInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, CreateFolderInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, CreateFolderInputs()));
        }

        [Test]
        public void CreateMaterial_Contract()
        {
            var id = CreateMaterialOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, CreateMaterialInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, CreateMaterialInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, CreateMaterialInputs()));
        }

        [Test]
        public void CreateScriptableObject_Contract()
        {
            var id = CreateScriptableObjectOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, CreateObjectInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, CreateObjectInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, CreateObjectInputs()));
        }

        [Test]
        public void Find_Contract()
        {
            var id = FindAssetsOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, FindInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, FindInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, FindInputs()));
        }

        [Test]
        public void Move_Contract()
        {
            var id = MoveAssetOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, MoveInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, MoveInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, MoveInputs()));
        }

        [Test]
        public void Copy_Contract()
        {
            var id = CopyAssetOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, CopyInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, CopyInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, CopyInputs()));
        }

        [Test]
        public void Delete_Contract()
        {
            var id = DeleteAssetOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, DeleteInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, DeleteInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, DeleteInputs()));
        }

        [Test]
        public void SaveAll_Contract()
        {
            var id = SaveAllOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, new object()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, new object()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, new object()));
        }

        // ── The deliberately weakened Reconcile fixture ─────────────────

        // The ticket's boundary acceptance: a Reconcilable Operation whose
        // Reconcile is weakened to answer Completed on the artifact's mere
        // existence — exactly the evidence SPEC §F5 rules out — must fail
        // the contract. The dummy below writes a file artifact like the
        // Asset-creating Operations do: its (a)/(b) judgements pass, and
        // the damaged-evidence probe's overwritten file is what exposes
        // the lie.
        [Test]
        public void WeakenedReconcile_IsDetected()
        {
            // The damaged-evidence probe must be the failing assertion —
            // an exception from an earlier before/after-Execute check would
            // prove nothing about the lie this fixture exists to expose, so
            // the verdict's specific §F5 rejection text is required, not
            // just any AssertionException.
            var failure = Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    "test.at02.weakened_asset_create",
                    new { path = Fixture().TempFolder + "/At02Weakened.asset" }));
            Assert.IsNotNull(failure);
            StringAssert.Contains("test.at02.weakened_asset_create", failure!.Message);
            StringAssert.Contains(
                "after its artifact was damaged", failure.Message);
            StringAssert.Contains("admit Unknown only", failure.Message);
        }

        // ── Fixture inputs ──────────────────────────────────────────────

        private OperationTestHarness Fixture()
        {
            if (_fixture is null)
            {
                _fixture = OperationTestHarness.Create();
                _harnesses.Add(_fixture);
            }
            return _fixture;
        }

        private string Unique(string stem) => stem + "-" + _tag + "-" + (++_serial);

        // unity.asset.import: a file that exists on disk and is not yet
        // imported — Execute's real ImportAsset is what turns it into an
        // asset, and the wait token survives to Poll. Each call writes a
        // fresh file: the assertions consume the import.
        private object ImportInputs()
        {
            var path = Fixture().TempFolder + "/" + Unique("At02Import") + ".txt";
            File.WriteAllText(PhysicalPath(path), "at-02 import fixture\n");
            return new { path };
        }

        private object CreateFolderInputs() =>
            new { path = Fixture().TempFolder + "/" + Unique("At02Folder") };

        // unity.asset.create_material: the probe Shader the positive
        // fixture resolves by name, with one declared property applied —
        // the deterministic serialized content the Reconcile row expects.
        private object CreateMaterialInputs() =>
            new
            {
                path = Fixture().TempFolder + "/" + Unique("At02Material") + ".mat",
                shader = CreateProbeShader(),
                properties = JsonValue.Parse("{\"_Metallic\":1}").Value!,
            };

        // unity.asset.create_scriptable_object: the test assembly's
        // ProbeFieldsAsset — the type_exists requirement resolves it in
        // the domain's type space and its default instance is the expected
        // serialized content.
        private object CreateObjectInputs() =>
            new
            {
                path = Fixture().TempFolder + "/" + Unique("At02Object") + ".asset",
                type = typeof(ProbeFieldsAsset).FullName!,
            };

        // unity.asset.find: a deterministic result set — the probe text
        // assets under the fixture folder — on every call.
        private object FindInputs()
        {
            CreateTextAsset(
                Fixture().TempFolder + "/" + Unique("At02Find") + ".txt",
                "at-02 find fixture\n");
            return new
            {
                filter = "t:TextAsset",
                folders = JsonValue.From(new JArray(Fixture().TempFolder)),
            };
        }

        // unity.asset.move: the §B5 AssetRef is captured, never the live
        // object — the Reconcile contexts the contract rebuilds must not
        // re-resolve an asset the step already moved.
        private object MoveInputs()
        {
            var source = Fixture().TempFolder + "/" + Unique("At02MoveSrc") + ".txt";
            CreateTextAsset(source, "at-02 move fixture\n");
            var asset = AssetDatabase.LoadMainAssetAtPath(source);
            Assert.IsNotNull(asset, "the fixture asset must import: " + source);
            return new
            {
                asset = new RefResolver().ToRef(asset!, "unity.AssetRef<unity.Object>"),
                destination = Fixture().TempFolder + "/" + Unique("At02MoveDst") + ".txt",
            };
        }

        private object CopyInputs()
        {
            var source = Fixture().TempFolder + "/" + Unique("At02CopySrc") + ".txt";
            CreateTextAsset(source, "at-02 copy fixture\n");
            var asset = AssetDatabase.LoadMainAssetAtPath(source);
            Assert.IsNotNull(asset, "the fixture asset must import: " + source);
            return new
            {
                asset = new RefResolver().ToRef(asset!, "unity.AssetRef<unity.Object>"),
                destination = Fixture().TempFolder + "/" + Unique("At02CopyDst") + ".txt",
            };
        }

        // unity.asset.delete: the captured AssetRef survives the trash —
        // the live object the ref named may be gone when the contract
        // rebuilds the Reconcile inputs.
        private object DeleteInputs()
        {
            var path = Fixture().TempFolder + "/" + Unique("At02Delete") + ".txt";
            CreateTextAsset(path, "at-02 delete fixture\n");
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            Assert.IsNotNull(asset, "the fixture asset must import: " + path);
            return new
            {
                asset = new RefResolver().ToRef(asset!, "unity.AssetRef<unity.Object>"),
            };
        }

        // ── Asset fixtures ──────────────────────────────────────────────

        // A real imported TextAsset at the project-relative path —
        // returns nothing: the AssetRef the caller publishes is captured
        // through RefResolver so no test re-resolves a moved or deleted
        // asset.
        private static void CreateTextAsset(string path, string contents)
        {
            File.WriteAllText(PhysicalPath(path), contents);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            Assert.That(guid, Is.Not.Empty,
                "the fixture asset must import: " + path);
        }

        // A real Shader asset declaring the §G7 item 4 property kinds —
        // the AssetCreateOperationTests probe, re-imported per call so a
        // stale folder from an earlier run can never serve it.
        private static string CreateProbeShader()
        {
            File.WriteAllText(PhysicalPath(ProbeShaderPath), ProbeShaderSource);
            AssetDatabase.ImportAsset(
                ProbeShaderPath, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(Shader.Find(ProbeShaderName), Is.Not.Null,
                "the probe shader must resolve by name: " + ProbeShaderName);
            return ProbeShaderName;
        }

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(
                ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));

        // The stand-in the harness's requirement evaluator makes for the
        // settings snapshot a Run's acceptance validated (SPEC §H4.1): the
        // project's live allowedPaths, or the empty set a settings file
        // that fails to load means — the fail-closed half of decision
        // D-R6. The Operations' internal allowed-paths seams take the same
        // shape, so the contract's path_writable judgement is the one a
        // Run with these settings would make.
        private static IReadOnlyList<string> HarnessAllowedPaths()
        {
            try
            {
                var loaded = OpWeaveSettingsLoader.Load();
                return loaded.Ok && loaded.Value is { } settings
                    ? settings.AllowedPaths
                    : (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        // ── Fixture Operations ─────────────────────────────────────────

        /// <summary>
        /// The deliberately weakened fixture of the ticket's boundary
        /// acceptance: shaped on the Asset-creating Operations of the §I.3
        /// group — a Reconcilable create whose Execute writes the artifact
        /// file — but its Reconcile answers
        /// <see cref="ReconcileResult.Completed"/> whenever a file exists
        /// at the recorded path. Existence and paths are the evidence SPEC
        /// §F5 explicitly rules out, so the damaged-evidence probe's
        /// overwritten file must make AssertIdempotency fail: the correct
        /// verdict there is Unknown only.
        /// </summary>
        [OpWeaveOperation("test.at02.weakened_asset_create", 1)]
        public sealed class WeakenedAssetCreateReconcileOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.at02.weakened_asset_create", 1)
                .Summary("Writes a file artifact; claims Completed on its existence.")
                .Input("path", "unity.AssetPath", required: true)
                .Effects(EffectNames.AssetCreate)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.at02.weakened_asset_create", 1,
                            "{\"path\":\"Assets/OpWeaveExamples/ProbeAsset.txt\"}"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject
                    {
                        ["path"] = ctx.Get<string>("path"),
                    }));

            public StepResult Execute(StepContext ctx)
            {
                var path = ctx.Get<string>("path");
                File.WriteAllText(
                    Path.Combine(Application.dataPath, "..", path),
                    "%YAML 1.1\n# weakened-fixture artifact — content is not evidence\n");
                ctx.Changes.Created(
                    new ChangeTarget("asset", path, null),
                    undoable: false);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var path = snapshot.ToToken()["path"]?.ToString();
                // The weakening this fixture exists for: Completed from the
                // file's mere presence — §F5's forbidden evidence.
                return path is not null && File.Exists(
                        Path.Combine(Application.dataPath, "..", path))
                    ? new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue>(StringComparer.Ordinal))
                    : (ReconcileResult)new ReconcileResult.NotApplied();
            }

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
