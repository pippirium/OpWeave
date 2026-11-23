#nullable enable

// T-W2.6-01 [Edit] tests (local:T-W2.6-01:prefab-fingerprint): SPEC §G6.1
// PrefabContentFingerprint v1 — pre-save vs post-save equality per verified
// state (L-15), single-change sensitivity, fail-closed Unknown on missing
// script/unresolvable reference, and the SG-12 golden bytes/hash.
//
// Root-name rule (local, documented): Unity names the saved Prefab root
// after the asset file, so the expected fingerprint is computed through
// ComputeForSave(root, destinationPath), which normalizes the root name
// to the destination file name — modelling exactly what Unity persists.
// SavedPrefabRoot_TakesAssetFileName verifies the rename behaviour and
// ComputeForSave_RootNameFollowsDestinationFile exercises a root named
// differently from its destination.
//
// Variant rule (SPEC §G6.1 実機検証): a content root that is itself a
// Prefab instance is Variant content; the v1 document covers it through
// the source-Prefab reference plus the persisted override set — the
// scene-side entries Unity never serializes (m_RootOrder) are excluded
// and the root's persisted m_Name override follows the destination file
// name — so Variant gets the same pre-save/post-save proof as
// Nested/Override. A failed Variant comparison Debug.Logs both
// Canonical JSON documents on single [FP-VARIANT-PRE] /
// [FP-VARIANT-POST] lines for the next round's diff.
//
// Per-editor golden rule (T-W2.6-06): the golden canonical bytes are real
// persisted content, not an arbitrary expected string, so each golden
// fixture is pinned per supported Unity editor (6000.0 and 6000.5) and
// selected from Application.unityVersion. Each pinned document comes from
// that editor's own run — never hand-edited. An editor without a pinned
// golden fails the golden tests with a message naming its version; it is
// never skipped or marked passed. Only the external-reference fixture
// differs between the two pinned editors (6000.5 persists a non-empty
// m_EditorClassIdentifier for the fixture MonoBehaviour where 6000.0
// persists an empty one); the normal and override fixtures are
// byte-identical on both and keep one shared constant each. The
// fingerprint algorithm, the normalization targets and the SG-12 v1
// document structure are unchanged — the Unity version is already part of
// the snapshot and a version mismatch is already Unknown (SPEC §G6.1).
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class PrefabContentFingerprintTests
    {
        private const string ProbeFolder = "Assets/OpWeaveFingerprintProbe";
        private const string NormalPath = ProbeFolder + "/normal.prefab";
        private const string InnerPath = ProbeFolder + "/inner.prefab";
        private const string NestedPath = ProbeFolder + "/nested.prefab";
        private const string BasePath = ProbeFolder + "/base.prefab";
        private const string SecondBasePath = ProbeFolder + "/secondbase.prefab";
        private const string VariantPath = ProbeFolder + "/variant.prefab";
        private const string OverridePath = ProbeFolder + "/override.prefab";
        private const string ManagedPath = ProbeFolder + "/managed.prefab";
        private const string ArraysPath = ProbeFolder + "/arrays.prefab";
        private const string ExternalPath = ProbeFolder + "/external.prefab";
        private const string LinkedAssetPath = ProbeFolder + "/linked.asset";
        private const string LinkedPrefabPath = ProbeFolder + "/linkedprefab.prefab";
        private const string MissingPath = ProbeFolder + "/missing.prefab";
        private const string DanglingPath = ProbeFolder + "/dangling.prefab";
        private const string GoldenPath = ProbeFolder + "/golden.prefab";
        private const string GoldenOvrPath = ProbeFolder + "/goldenovr.prefab";
        private const string GoldenExtPath = ProbeFolder + "/goldenext.prefab";
        private const string GoldenInnerPath = ProbeFolder + "/goldeninner.prefab";

        // The guid pinned into goldeninner.prefab.meta: a runtime-created
        // prefab would carry a random guid, so the override/reference
        // goldens write the asset's .meta themselves.
        private const string GoldenInnerGuid = "3a8f1c4e7b2d49568e0f1a3b5c7d9e2f";

        private readonly List<GameObject> _created = new List<GameObject>();

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_created[i]);
                }
            }
            _created.Clear();
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            AssetDatabase.Refresh();
        }

        private GameObject Go(string name, GameObject? parent = null)
        {
            var go = new GameObject(name);
            if (parent is not null)
            {
                go.transform.SetParent(parent.transform, worldPositionStays: false);
            }
            _created.Add(go);
            return go;
        }

        private static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveFingerprintProbe");
            }
        }

        private GameObject SavePrefab(GameObject go, string path)
        {
            EnsureFolder();
            var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
            Assert.IsNotNull(saved, "fixture prefab must save");
            return saved;
        }

        // Writes a hand-authored asset plus its .meta under ProbeFolder and
        // imports it synchronously — the way the goldens pin guids and
        // local file IDs a runtime-created asset could not fix.
        private static void WriteImportedAsset(string assetPath, string yaml, string meta)
        {
            var full = Path.Combine(
                ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(full, yaml);
            File.WriteAllText(full + ".meta", meta);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static string ReasonOf(PrefabFingerprintResult result) =>
            result is PrefabFingerprintResult.Unknown u ? u.Reason : "<computed>";

        // SPEC §G6.1 test: the Preflight fingerprint of the to-be-saved
        // content (what open_contents holds before save_contents writes
        // it) must equal the recovery fingerprint of the saved asset.
        // The pre-save read is destination-aware — see the root-name
        // rule in the file header. A failed comparison prints both
        // Canonical JSON documents so the differing field is visible.
        private static void AssertSaveMatches(
            GameObject toBeSaved, string path, string? diagnosticPrefix = null)
        {
            EnsureFolder();
            var expectedResult = PrefabContentFingerprint.ComputeForSave(toBeSaved, path);
            var expected = expectedResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(expected,
                "pre-save fingerprint must compute: " + ReasonOf(expectedResult));

            var saved = PrefabUtility.SaveAsPrefabAsset(toBeSaved, path);
            Assert.IsNotNull(saved, "fixture prefab must save");

            var actualResult = PrefabContentFingerprint.ComputeAsset(path);
            var actual = actualResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(actual,
                "post-save fingerprint must compute: " + ReasonOf(actualResult));
            var expectedDoc = expected!.Fingerprint.Document.CanonicalText;
            var actualDoc = actual!.Fingerprint.Document.CanonicalText;
            if (diagnosticPrefix is not null &&
                !string.Equals(expectedDoc, actualDoc, StringComparison.Ordinal))
            {
                Debug.Log("[" + diagnosticPrefix + "-PRE]" + expectedDoc);
                Debug.Log("[" + diagnosticPrefix + "-POST]" + actualDoc);
            }
            Assert.That(actualDoc, Is.EqualTo(expectedDoc),
                "pre-save and post-save fingerprints must be identical" +
                "\n--- pre-save document ---\n" + expectedDoc +
                "\n--- post-save document ---\n" + actualDoc);
        }

        // ── SPEC §G6.1 / L-15 verified states ───────────────────────────

        [Test]
        public void Normal_PreSaveEqualsPostSave()
        {
            var root = Go("normal");
            var comp = FingerprintFixtureAccess.AddBehaviour(root);
            FingerprintFixtureAccess.Edit(comp, so =>
            {
                so.FindProperty("number").intValue = 7;
                so.FindProperty("label").stringValue = "state";
                so.FindProperty("amount").floatValue = 1.5f;
                so.FindProperty("offset").vector3Value = new Vector3(1f, 2f, 3f);
            });
            var child = Go("FpNormalChild", root);
            child.transform.localPosition = new Vector3(0.25f, -0.5f, 2f);
            AssertSaveMatches(root, NormalPath);
        }

        [Test]
        public void Nested_PreSaveEqualsPostSave()
        {
            var inner = Go("inner");
            var innerComp = FingerprintFixtureAccess.AddBehaviour(inner);
            FingerprintFixtureAccess.Edit(innerComp,
                so => so.FindProperty("number").intValue = 3);
            SavePrefab(inner, InnerPath);
            var outer = Go("nested");
            var innerAsset = AssetDatabase.LoadAssetAtPath<GameObject>(InnerPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset)!;
            _created.Add(instance);
            instance.transform.SetParent(outer.transform, worldPositionStays: false);
            AssertSaveMatches(outer, NestedPath);
        }

        // SPEC §G6.1 実機検証: a Variant root (the content root is
        // itself an instance of its base) is normalized through its
        // source-Prefab reference and persisted override set, so both
        // sides of the save must produce the identical document like
        // every proven state. A failed comparison logs both documents
        // on single [FP-VARIANT-PRE]/[FP-VARIANT-POST] lines (file
        // header) so the differing field is visible next round.
        [Test]
        public void Variant_PreSaveEqualsPostSave()
        {
            var baseGo = Go("base");
            var baseComp = FingerprintFixtureAccess.AddBehaviour(baseGo);
            FingerprintFixtureAccess.Edit(baseComp,
                so => so.FindProperty("number").intValue = 9);
            SavePrefab(baseGo, BasePath);
            var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BasePath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(baseAsset)!;
            _created.Add(instance);
            instance.name = "variant";
            AssertSaveMatches(instance, VariantPath, "FP-VARIANT");
        }

        // The root-name rule covers Variant too: SaveAsPrefabAsset names
        // the saved Variant root after the destination file, so the
        // pre-save document normalizes the root's m_Name — including the
        // persisted m_Name override the Variant records on the root's
        // source GameObject — to the file name.
        [Test]
        public void Variant_RootNameFollowsDestinationFile()
        {
            var baseGo = Go("base");
            var baseComp = FingerprintFixtureAccess.AddBehaviour(baseGo);
            FingerprintFixtureAccess.Edit(baseComp,
                so => so.FindProperty("number").intValue = 9);
            SavePrefab(baseGo, BasePath);
            var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BasePath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(baseAsset)!;
            _created.Add(instance);
            instance.name = "FpVariantSceneSideName";
            AssertSaveMatches(instance, VariantPath, "FP-VARIANT");
        }

        // SPEC §G6.1 単一変更検出: an overridden property on the
        // Variant's inherited component enters the persisted
        // modification set, so two Variants differing only in that
        // override must produce different fingerprints.
        [Test]
        public void VariantOverrideChange_ChangesFingerprint()
        {
            var baseGo = Go("base");
            var baseComp = FingerprintFixtureAccess.AddBehaviour(baseGo);
            FingerprintFixtureAccess.Edit(baseComp,
                so => so.FindProperty("number").intValue = 9);
            SavePrefab(baseGo, BasePath);
            GameObject MakeVariant(int number)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(BasePath);
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset)!;
                _created.Add(inst);
                inst.name = "variant";
                var comp = FingerprintFixtureAccess.Behaviour(inst);
                FingerprintFixtureAccess.Edit(comp,
                    so => so.FindProperty("number").intValue = number);
                PrefabUtility.RecordPrefabInstancePropertyModifications(inst);
                return inst;
            }
            var first = PrefabContentFingerprint.Compute(MakeVariant(1));
            var second = PrefabContentFingerprint.Compute(MakeVariant(2));
            var a = first as PrefabFingerprintResult.Computed;
            var b = second as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(a, ReasonOf(first));
            Assert.IsNotNull(b, ReasonOf(second));
            Assert.That(b!.Fingerprint.Hash, Is.Not.EqualTo(a!.Fingerprint.Hash));
        }

        // The base (source) Prefab reference enters the Variant's
        // document, so a Variant of a different base must produce a
        // different fingerprint even when the content is otherwise
        // identical.
        [Test]
        public void VariantBaseChange_ChangesFingerprint()
        {
            var baseGo = Go("base");
            var baseComp = FingerprintFixtureAccess.AddBehaviour(baseGo);
            FingerprintFixtureAccess.Edit(baseComp,
                so => so.FindProperty("number").intValue = 9);
            SavePrefab(baseGo, BasePath);
            var secondBase = Go("secondbase");
            var secondComp = FingerprintFixtureAccess.AddBehaviour(secondBase);
            FingerprintFixtureAccess.Edit(secondComp,
                so => so.FindProperty("number").intValue = 9);
            SavePrefab(secondBase, SecondBasePath);
            var first = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(BasePath))!;
            var second = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(SecondBasePath))!;
            _created.Add(first);
            _created.Add(second);
            first.name = "variant";
            second.name = "variant";

            var a = PrefabContentFingerprint.Compute(first);
            var b = PrefabContentFingerprint.Compute(second);
            var aComputed = a as PrefabFingerprintResult.Computed;
            var bComputed = b as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(aComputed, ReasonOf(a));
            Assert.IsNotNull(bComputed, ReasonOf(b));
            Assert.That(bComputed!.Fingerprint.Hash,
                Is.Not.EqualTo(aComputed!.Fingerprint.Hash));
        }

        [Test]
        public void Override_PreSaveEqualsPostSave()
        {
            SavePrefab(Go("inner"), InnerPath);
            var outer = Go("override");
            var innerAsset = AssetDatabase.LoadAssetAtPath<GameObject>(InnerPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset)!;
            _created.Add(instance);
            instance.transform.SetParent(outer.transform, worldPositionStays: false);
            instance.name = "FpRenamedInstance";
            var comp = FingerprintFixtureAccess.AddBehaviour(instance);
            FingerprintFixtureAccess.Edit(comp,
                so => so.FindProperty("number").intValue = 21);
            PrefabUtility.RecordPrefabInstancePropertyModifications(instance);
            AssertSaveMatches(outer, OverridePath);
        }

        [Test]
        public void ManagedReference_PreSaveEqualsPostSave()
        {
            var root = Go("managed");
            var comp = FingerprintFixtureAccess.AddBehaviour(root);
            FingerprintFixtureAccess.Edit(comp, so =>
            {
                so.FindProperty("managed").managedReferenceValue =
                    FingerprintFixtureAccess.NewManagedImpl(4, 0.75f);
            });
            AssertSaveMatches(root, ManagedPath);
        }

        [Test]
        public void Arrays_PreSaveEqualsPostSave()
        {
            var root = Go("arrays");
            var comp = FingerprintFixtureAccess.AddBehaviour(root);
            FingerprintFixtureAccess.Edit(comp, so =>
            {
                var numbers = so.FindProperty("numbers");
                numbers.arraySize = 3;
                numbers.GetArrayElementAtIndex(0).intValue = 1;
                numbers.GetArrayElementAtIndex(1).intValue = 2;
                numbers.GetArrayElementAtIndex(2).intValue = 3;
                var nested = so.FindProperty("nested");
                nested.arraySize = 2;
                var first = nested.GetArrayElementAtIndex(0);
                first.FindPropertyRelative("amount").floatValue = 1.5f;
                first.FindPropertyRelative("note").stringValue = "a";
                var second = nested.GetArrayElementAtIndex(1);
                second.FindPropertyRelative("amount").floatValue = -2.25f;
                second.FindPropertyRelative("note").stringValue = "b";
            });
            AssertSaveMatches(root, ArraysPath);
        }

        [Test]
        public void ExternalReferences_PreSaveEqualsPostSave()
        {
            EnsureFolder();
            var linked = FingerprintFixtureAccess.NewLinkedAsset();
            AssetDatabase.CreateAsset(linked, LinkedAssetPath);
            var linkedPrefab = SavePrefab(Go("linkedprefab"), LinkedPrefabPath);
            var root = Go("external");
            var comp = FingerprintFixtureAccess.AddBehaviour(root);
            FingerprintFixtureAccess.Edit(comp, so =>
            {
                so.FindProperty("linkedAsset").objectReferenceValue = linked;
                so.FindProperty("linkedObject").objectReferenceValue = linkedPrefab;
            });
            AssertSaveMatches(root, ExternalPath);
        }

        // ── SPEC §G6.1 test: a single change changes the fingerprint ────

        [Test]
        public void SinglePropertyChange_ChangesFingerprint()
        {
            var root = Go("normal");
            var comp = FingerprintFixtureAccess.AddBehaviour(root);
            FingerprintFixtureAccess.Edit(comp,
                so => so.FindProperty("number").intValue = 7);
            var baselineResult = PrefabContentFingerprint.Compute(root);
            var baseline = baselineResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(baseline, ReasonOf(baselineResult));
            SavePrefab(root, NormalPath);

            var contents = PrefabUtility.LoadPrefabContents(NormalPath);
            try
            {
                var c = FingerprintFixtureAccess.Behaviour(contents);
                FingerprintFixtureAccess.Edit(c,
                    so => so.FindProperty("number").intValue = 8);
                var changed = PrefabContentFingerprint.Compute(contents);
                var computed = changed as PrefabFingerprintResult.Computed;
                Assert.IsNotNull(computed, ReasonOf(changed));
                Assert.That(computed!.Fingerprint.Hash,
                    Is.Not.EqualTo(baseline!.Fingerprint.Hash));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void ChildOrderChange_ChangesFingerprint()
        {
            var root = Go("normal");
            Go("FpFirst", root);
            Go("FpSecond", root);
            var baselineResult = PrefabContentFingerprint.Compute(root);
            var baseline = baselineResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(baseline, ReasonOf(baselineResult));
            SavePrefab(root, NormalPath);

            var contents = PrefabUtility.LoadPrefabContents(NormalPath);
            try
            {
                contents.transform.GetChild(0).SetSiblingIndex(1);
                var result = PrefabContentFingerprint.Compute(contents);
                var computed = result as PrefabFingerprintResult.Computed;
                Assert.IsNotNull(computed, ReasonOf(result));
                Assert.That(computed!.Fingerprint.Hash,
                    Is.Not.EqualTo(baseline!.Fingerprint.Hash));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        [Test]
        public void OverrideChange_ChangesFingerprint()
        {
            SavePrefab(Go("inner"), InnerPath);
            GameObject MakeOuter(string name, int value)
            {
                var outer = Go(name);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(InnerPath);
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(asset)!;
                _created.Add(inst);
                inst.transform.SetParent(outer.transform, worldPositionStays: false);
                var comp = FingerprintFixtureAccess.AddBehaviour(inst);
                FingerprintFixtureAccess.Edit(comp,
                    so => so.FindProperty("number").intValue = value);
                PrefabUtility.RecordPrefabInstancePropertyModifications(inst);
                return outer;
            }
            var first = MakeOuter("FpOuterA", 1);
            var second = MakeOuter("FpOuterB", 2);
            var a = PrefabContentFingerprint.Compute(first);
            var b = PrefabContentFingerprint.Compute(second);
            var aHash = ((PrefabFingerprintResult.Computed)a).Fingerprint.Hash;
            var bHash = ((PrefabFingerprintResult.Computed)b).Fingerprint.Hash;
            Assert.That(bHash, Is.Not.EqualTo(aHash));
        }

        // ── SPEC §G6.1 test: Missing Script / unresolvable reference ────

        [Test]
        public void MissingScript_IsUnknown()
        {
            EnsureFolder();
            // A prefab whose MonoBehaviour points at a script guid that does
            // not exist; Unity keeps the component slot as a missing script.
            var yaml =
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
                "  - component: {fileID: 114}\n" +
                "  m_Layer: 0\n" +
                "  m_Name: FpMissing\n" +
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
                "  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}\n" +
                "--- !u!114 &114\n" +
                "MonoBehaviour:\n" +
                "  m_ObjectHideFlags: 0\n" +
                "  m_CorrespondingSourceObject: {fileID: 0}\n" +
                "  m_PrefabInstance: {fileID: 0}\n" +
                "  m_PrefabAsset: {fileID: 0}\n" +
                "  m_GameObject: {fileID: 100}\n" +
                "  m_Enabled: 1\n" +
                "  m_EditorHideFlags: 0\n" +
                "  m_Script: {fileID: 11500000, guid: 0000000000000000000000000000ffff, type: 3}\n" +
                "  m_Name: \n" +
                "  m_EditorClassIdentifier: \n";
            File.WriteAllText(
                Path.Combine(ProjectRoot, MissingPath.Replace('/', Path.DirectorySeparatorChar)),
                yaml);
            AssetDatabase.ImportAsset(MissingPath, ImportAssetOptions.ForceSynchronousImport);

            var result = PrefabContentFingerprint.ComputeAsset(MissingPath);
            Assert.IsInstanceOf<PrefabFingerprintResult.Unknown>(result,
                "a missing script must fail closed into Unknown");
        }

        [Test]
        public void UnresolvableReference_IsUnknown()
        {
            EnsureFolder();
            var linked = FingerprintFixtureAccess.NewLinkedAsset();
            AssetDatabase.CreateAsset(linked, LinkedAssetPath);
            var root = Go("dangling");
            var comp = FingerprintFixtureAccess.AddBehaviour(root);
            FingerprintFixtureAccess.Edit(comp,
                so => so.FindProperty("linkedAsset").objectReferenceValue = linked);
            SavePrefab(root, DanglingPath);
            Assert.IsTrue(AssetDatabase.DeleteAsset(LinkedAssetPath),
                "the referenced asset must be deleted");
            AssetDatabase.Refresh();

            var result = PrefabContentFingerprint.ComputeAsset(DanglingPath);
            Assert.IsInstanceOf<PrefabFingerprintResult.Unknown>(result,
                "a dangling object reference must fail closed into Unknown");
        }

        // ── SPEC §G6.1 snapshot serialization conditions ────────────────

        // F1: the snapshot records the project's serialization conditions
        // (EditorSettings.serializationMode, assetPipelineMode and
        // serializeInlineMappingsOnOneLine); Check may answer Matched only
        // when every recorded condition equals the live one — a different,
        // missing or unparseable condition fails closed into Unknown.
        [Test]
        public void Check_RecordedConditionsMatch_IsMatched()
        {
            var root = Go("normal");
            var result = PrefabContentFingerprint.Compute(root);
            var computed = result as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(computed,
                "fingerprint must compute: " + ReasonOf(result));
            var snapshot = PrefabContentFingerprint.Snapshot(
                computed!.Fingerprint, targetGuid: null, targetPath: null);
            var verdict = PrefabContentFingerprint.Check(snapshot, root);
            Assert.IsInstanceOf<PrefabFingerprintCheck.Matched>(verdict,
                "a snapshot taken in this Editor must match the live content");
        }

        [Test]
        public void Check_DifferentSerializationMode_IsUnknown()
        {
            var root = Go("normal");
            var result = PrefabContentFingerprint.Compute(root);
            var computed = result as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(computed,
                "fingerprint must compute: " + ReasonOf(result));
            var snapshot = (JObject)PrefabContentFingerprint.Snapshot(
                computed!.Fingerprint, targetGuid: null, targetPath: null).ToToken();
            var serialization = (JObject)snapshot["serialization"]!;
            var recorded = serialization["serializationMode"]!.ToString();
            var forged = string.Empty;
            foreach (var name in Enum.GetNames(typeof(SerializationMode)))
            {
                if (!string.Equals(name, recorded, StringComparison.Ordinal))
                {
                    forged = name;
                    break;
                }
            }
            Assert.AreNotEqual(recorded, forged,
                "SerializationMode must offer a second value to forge");
            serialization["serializationMode"] = forged;

            var verdict = PrefabContentFingerprint.Check(
                JsonValue.From(snapshot), root);
            var unknown = verdict as PrefabFingerprintCheck.Unknown;
            Assert.IsNotNull(unknown,
                "a different serializationMode must fail closed, never Matched");
            Assert.That(unknown!.Reason, Does.Contain("serializationMode"));
        }

        [Test]
        public void Check_MissingSerializationMode_IsUnknown()
        {
            var root = Go("normal");
            var result = PrefabContentFingerprint.Compute(root);
            var computed = result as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(computed,
                "fingerprint must compute: " + ReasonOf(result));
            var snapshot = (JObject)PrefabContentFingerprint.Snapshot(
                computed!.Fingerprint, targetGuid: null, targetPath: null).ToToken();
            ((JObject)snapshot["serialization"]!).Remove("serializationMode");

            var verdict = PrefabContentFingerprint.Check(
                JsonValue.From(snapshot), root);
            var unknown = verdict as PrefabFingerprintCheck.Unknown;
            Assert.IsNotNull(unknown,
                "a missing serializationMode condition must fail closed, never Matched");
            Assert.That(unknown!.Reason, Does.Contain("serializationMode"));
        }

        [Test]
        public void Check_MissingSerializationConditions_IsUnknown()
        {
            var root = Go("normal");
            var result = PrefabContentFingerprint.Compute(root);
            var computed = result as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(computed,
                "fingerprint must compute: " + ReasonOf(result));
            var snapshot = (JObject)PrefabContentFingerprint.Snapshot(
                computed!.Fingerprint, targetGuid: null, targetPath: null).ToToken();
            snapshot.Remove("serialization");

            var verdict = PrefabContentFingerprint.Check(
                JsonValue.From(snapshot), root);
            Assert.IsInstanceOf<PrefabFingerprintCheck.Unknown>(verdict,
                "a snapshot without serialization conditions must fail closed, never Matched");
        }

        // ── SG-12 golden bytes/hash ─────────────────────────────────────

        // F3: the production Preflight read is destination-aware — a
        // to-be-saved root whose scene-side name differs from the
        // destination file still produces the post-save fingerprint
        // because ComputeForSave normalizes the root name to the file
        // name Unity persists (the chosen rule; see file header).
        [Test]
        public void ComputeForSave_RootNameFollowsDestinationFile()
        {
            EnsureFolder();
            var root = Go("FpSceneSideName");
            var comp = FingerprintFixtureAccess.AddBehaviour(root);
            FingerprintFixtureAccess.Edit(comp,
                so => so.FindProperty("number").intValue = 11);
            var destination = ProbeFolder + "/renamed.prefab";

            var expectedResult = PrefabContentFingerprint.ComputeForSave(root, destination);
            var expected = expectedResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(expected,
                "destination-aware fingerprint must compute: " + ReasonOf(expectedResult));
            SavePrefab(root, destination);
            var actualResult = PrefabContentFingerprint.ComputeAsset(destination);
            var actual = actualResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(actual,
                "post-save fingerprint must compute: " + ReasonOf(actualResult));

            Assert.That(actual!.Fingerprint.Document.CanonicalText,
                Is.EqualTo(expected!.Fingerprint.Document.CanonicalText),
                "a differently named root must still produce the post-save fingerprint");
            Assert.That(expected.Fingerprint.Document.CanonicalText,
                Does.Contain("\"m_Name\":\"renamed\""),
                "the pre-save document must carry the destination file name");
        }

        [Test]
        public void SavedPrefabRoot_TakesAssetFileName()
        {
            var root = Go("FpSceneName");
            SavePrefab(root, GoldenPath);
            var contents = PrefabUtility.LoadPrefabContents(GoldenPath);
            try
            {
                Assert.That(contents.name, Is.EqualTo("golden"),
                    "Unity names the saved Prefab root after the asset file");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        // The Unity editors whose golden bytes and hashes are pinned in this
        // file, keyed by the major.minor of Application.unityVersion.
        private const string PinnedEditor6000_0 = "6000.0";
        private const string PinnedEditor6000_5 = "6000.5";

        private static readonly string[] PinnedEditorMajorMinors =
            { PinnedEditor6000_0, PinnedEditor6000_5 };

        // "6000.0.85f1" -> "6000.0", "6000.5.11f1" -> "6000.5". A version
        // without two dotted components is returned unchanged so the failure
        // message can name it exactly as the Editor reported it.
        private static string EditorMajorMinor(string unityVersion)
        {
            var first = unityVersion.IndexOf('.');
            if (first < 0)
            {
                return unityVersion;
            }
            var second = unityVersion.IndexOf('.', first + 1);
            return second < 0 ? unityVersion : unityVersion.Substring(0, second);
        }

        private static bool IsPinnedGoldenEditor(string unityVersion)
        {
            var majorMinor = EditorMajorMinor(unityVersion);
            foreach (var pinned in PinnedEditorMajorMinors)
            {
                if (string.Equals(pinned, majorMinor, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // An Editor nobody recorded golden bytes for must fail the golden
        // tests: a skipped or silently passing golden would report a real
        // fingerprint difference as success, so the guard asserts instead of
        // returning a default document.
        private static void AssertPinnedGoldenEditor(string unityVersion)
        {
            if (IsPinnedGoldenEditor(unityVersion))
            {
                return;
            }
            Assert.Fail(
                "The golden fingerprint fixtures pin Unity editor versions " +
                string.Join(", ", PinnedEditorMajorMinors) +
                "; this editor reports version " + unityVersion +
                ". Pin this editor's golden bytes and hash before running" +
                " the golden tests on it.");
        }

        // The external-reference fixture is the only golden whose pinned
        // bytes differ by Editor — the callers have already failed every
        // other Editor, so false means the 6000.0 constants.
        private static bool Uses6000_5Golden(string unityVersion) =>
            string.Equals(
                EditorMajorMinor(unityVersion), PinnedEditor6000_5,
                StringComparison.Ordinal);

        [Test]
        public void GoldenBytesAndHash_AreFixed()
        {
            // The normal fixture's document is byte-identical on both pinned
            // editors; the guard still fails any other editor.
            AssertPinnedGoldenEditor(Application.unityVersion);
            var root = Go("golden");
            var expectedResult = PrefabContentFingerprint.Compute(root);
            var expected = expectedResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(expected,
                "pre-save fingerprint must compute: " + ReasonOf(expectedResult));
            SavePrefab(root, GoldenPath);
            var actualResult = PrefabContentFingerprint.ComputeAsset(GoldenPath);
            var actual = actualResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(actual,
                "post-save fingerprint must compute: " + ReasonOf(actualResult));

            Assert.That(actual!.Fingerprint.Version, Is.EqualTo(1));
            Assert.That(expected!.Fingerprint.Hash,
                Is.EqualTo(actual.Fingerprint.Hash),
                "pre-save and post-save fingerprints must be identical");
            Assert.That(actual.Fingerprint.Document.CanonicalText,
                Is.EqualTo(GoldenCanonical),
                "golden canonical bytes changed; actual document: " +
                actual.Fingerprint.Document.CanonicalText);
            Assert.That(actual.Fingerprint.Hash, Is.EqualTo(GoldenHash),
                "golden hash changed; actual hash: " + actual.Fingerprint.Hash);
        }

        // SG-12 pinned v1 document for the override state: a nested
        // instance of the guid-pinned inner prefab carrying one
        // overridden property (m_Name) plus the instance transform
        // modification set — locking the v1 overrides representation.
        [Test]
        public void GoldenOverride_BytesAndHashAreFixed()
        {
            // The override fixture's document is byte-identical on both
            // pinned editors; the guard still fails any other editor.
            AssertPinnedGoldenEditor(Application.unityVersion);
            EnsureFolder();
            WriteImportedAsset(GoldenInnerPath, GoldenInnerYaml, GoldenInnerMeta);
            var innerAsset = AssetDatabase.LoadAssetAtPath<GameObject>(GoldenInnerPath);
            Assert.IsNotNull(innerAsset, "the pinned inner prefab must import");
            var outer = Go("goldenovr");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset)!;
            _created.Add(instance);
            instance.transform.SetParent(outer.transform, worldPositionStays: false);
            instance.name = "FpGoldenInstance";
            PrefabUtility.RecordPrefabInstancePropertyModifications(instance);

            var expectedResult = PrefabContentFingerprint.Compute(outer);
            var expected = expectedResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(expected,
                "pre-save fingerprint must compute: " + ReasonOf(expectedResult));
            SavePrefab(outer, GoldenOvrPath);
            var actualResult = PrefabContentFingerprint.ComputeAsset(GoldenOvrPath);
            var actual = actualResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(actual,
                "post-save fingerprint must compute: " + ReasonOf(actualResult));

            Assert.That(actual!.Fingerprint.Hash,
                Is.EqualTo(expected!.Fingerprint.Hash),
                "pre-save and post-save fingerprints must be identical" +
                "\n--- pre-save document ---\n" +
                expected.Fingerprint.Document.CanonicalText +
                "\n--- post-save document ---\n" +
                actual.Fingerprint.Document.CanonicalText);
            Assert.That(actual.Fingerprint.Document.CanonicalText,
                Is.EqualTo(GoldenOverrideCanonical),
                "override golden canonical bytes changed" +
                "\n--- actual document ---\n" +
                actual.Fingerprint.Document.CanonicalText);
            Assert.That(actual.Fingerprint.Hash, Is.EqualTo(GoldenOverrideHash),
                "override golden hash changed; actual hash: " +
                actual.Fingerprint.Hash);
        }

        // SG-12 pinned v1 document for an external asset reference:
        // linkedAsset and m_Script both point at the fixture MonoScript,
        // whose guid (and localFileId 11500000) is fixed by its
        // checked-in .meta — locking the v1 references representation.
        // This is the one fixture whose persisted bytes differ between the
        // pinned editors (the fixture MonoBehaviour's
        // m_EditorClassIdentifier), so its expected document and hash are
        // selected from Application.unityVersion.
        [Test]
        public void GoldenExternalReference_BytesAndHashAreFixed()
        {
            var unityVersion = Application.unityVersion;
            AssertPinnedGoldenEditor(unityVersion);
            var uses6000_5Golden = Uses6000_5Golden(unityVersion);
            var pinnedCanonical = uses6000_5Golden
                ? GoldenExternalCanonical6000_5
                : GoldenExternalCanonical;
            var pinnedHash = uses6000_5Golden
                ? GoldenExternalHash6000_5
                : GoldenExternalHash;

            var root = Go("goldenext");
            var comp = FingerprintFixtureAccess.AddBehaviour(root);
            var script = MonoScript.FromMonoBehaviour((MonoBehaviour)comp);
            FingerprintFixtureAccess.Edit(comp, so =>
                so.FindProperty("linkedAsset").objectReferenceValue = script);

            var expectedResult = PrefabContentFingerprint.Compute(root);
            var expected = expectedResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(expected,
                "pre-save fingerprint must compute: " + ReasonOf(expectedResult));
            SavePrefab(root, GoldenExtPath);
            var actualResult = PrefabContentFingerprint.ComputeAsset(GoldenExtPath);
            var actual = actualResult as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(actual,
                "post-save fingerprint must compute: " + ReasonOf(actualResult));

            Assert.That(actual!.Fingerprint.Hash,
                Is.EqualTo(expected!.Fingerprint.Hash),
                "pre-save and post-save fingerprints must be identical" +
                "\n--- pre-save document ---\n" +
                expected.Fingerprint.Document.CanonicalText +
                "\n--- post-save document ---\n" +
                actual.Fingerprint.Document.CanonicalText);
            Assert.That(actual.Fingerprint.Document.CanonicalText,
                Is.EqualTo(pinnedCanonical),
                "external-reference golden canonical bytes changed for Unity " +
                "editor version " + unityVersion +
                "\n--- actual document ---\n" +
                actual.Fingerprint.Document.CanonicalText);
            Assert.That(actual.Fingerprint.Hash, Is.EqualTo(pinnedHash),
                "external-reference golden hash changed for Unity editor " +
                "version " + unityVersion + "; actual hash: " +
                actual.Fingerprint.Hash);
        }

        // T-W2.6-06 boundary: the per-editor selection must fail — never
        // skip, never pass — on an editor whose golden bytes nobody
        // recorded. Only the two pinned editors run here, so the guard and
        // the selector are driven with recorded version strings.
        [Test]
        public void GoldenFixtureSelection_UnpinnedEditorVersion_FailsAndNeverSkips()
        {
            Assert.IsTrue(IsPinnedGoldenEditor("6000.0.85f1"),
                "Unity 6000.0 must select a pinned golden");
            Assert.IsTrue(IsPinnedGoldenEditor("6000.5.11f1"),
                "Unity 6000.5 must select a pinned golden");
            Assert.IsFalse(IsPinnedGoldenEditor("6000.1.0f1"),
                "an editor with no pinned golden must not select a golden");
            Assert.IsFalse(IsPinnedGoldenEditor("2022.3.62f1"),
                "an editor with no pinned golden must not select a golden");
            Assert.IsFalse(IsPinnedGoldenEditor("6001.0.0f1"),
                "a newer editor with no pinned golden must not select one");

            Assert.IsTrue(Uses6000_5Golden("6000.5.11f1"),
                "Unity 6000.5 must select the 6000.5 external-reference golden");
            Assert.IsFalse(Uses6000_5Golden("6000.0.85f1"),
                "Unity 6000.0 must select the golden pinned by T-W2.6-01");

            Exception? failure = null;
            try
            {
                AssertPinnedGoldenEditor("2022.3.62f1");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            Assert.IsInstanceOf<AssertionException>(failure,
                "an editor without a pinned golden must fail, never skip");
            Assert.That(failure!.Message, Does.Contain("2022.3.62f1"),
                "the failure message must name the unpinned editor version");
        }

        // Traversal-bound regression: a serialized array element must
        // carry exactly its own children. On Unity 6000 the
        // GetEndProperty sentinel lands after the array's last child for
        // an element property, so an unbounded child walk absorbs the
        // following sibling element under a bogus "data" member — the
        // defect independent review F1 observed on
        // GameObject.m_Component. The two assertions pin the corrected
        // shapes: one ComponentPair entry per attached component holding
        // only "component", and each element of the fixture's
        // serializable-class array holding only its own fields.
        [Test]
        public void ArrayElements_CarryOnlyTheirOwnMembers()
        {
            var root = Go("shapes");
            var comp = FingerprintFixtureAccess.AddBehaviour(root);
            FingerprintFixtureAccess.Edit(comp, so =>
            {
                var nested = so.FindProperty("nested");
                nested.arraySize = 2;
                var first = nested.GetArrayElementAtIndex(0);
                first.FindPropertyRelative("amount").floatValue = 1.5f;
                first.FindPropertyRelative("note").stringValue = "a";
                var second = nested.GetArrayElementAtIndex(1);
                second.FindPropertyRelative("amount").floatValue = -2.25f;
                second.FindPropertyRelative("note").stringValue = "b";
            });

            var result = PrefabContentFingerprint.Compute(root);
            var computed = result as PrefabFingerprintResult.Computed;
            Assert.IsNotNull(computed,
                "fingerprint must compute: " + ReasonOf(result));
            var document = (JObject)computed!.Fingerprint.Document.ToToken();

            var pairs = (JArray)((JObject)((JArray)document["hierarchy"]!)[0]!)
                ["properties"]!["m_Component"]!;
            Assert.That(pairs.Count, Is.EqualTo(2),
                "m_Component must carry one element per attached component");
            for (var i = 0; i < pairs.Count; i++)
            {
                var pair = (JObject)pairs[i]!;
                Assert.That(pair.Count, Is.EqualTo(1),
                    "a ComponentPair element must hold only the 'component' member");
                Assert.IsNotNull(pair.Property("component"),
                    "a ComponentPair element must hold a 'component' member");
                Assert.That((int)pair["component"]!["internal"]!["component"]!,
                    Is.EqualTo(i),
                    "m_Component element " + i + " must reference component index " + i);
            }

            var components = (JArray)document["components"]!;
            var behaviour = (JObject)components[1]!;
            Assert.That((string)behaviour["type"]!,
                Is.EqualTo("OpWeave.Fixtures.FingerprintFixtureBehaviour"));
            var elements = (JArray)((JObject)behaviour["properties"]!)["nested"]!;
            Assert.That(elements.Count, Is.EqualTo(2));
            for (var i = 0; i < elements.Count; i++)
            {
                var element = (JObject)elements[i]!;
                Assert.That(element.Count, Is.EqualTo(2),
                    "a struct-array element must carry only its own serialized fields");
                Assert.IsNotNull(element.Property("amount"));
                Assert.IsNotNull(element.Property("note"));
            }
            Assert.That((string)((JObject)elements[0]!)["note"]!, Is.EqualTo("a"));
            Assert.That((string)((JObject)elements[1]!)["note"]!, Is.EqualTo("b"));
        }

        // SG-12 pinned v1 document for a single GameObject (named after
        // the asset file — see the root-name rule) + Transform at
        // defaults: hierarchy/components/overrides/references members.
        // Recorded on Unity 6000.0 and byte-identical on Unity 6000.5, so
        // both pinned editors share this constant.
        private const string GoldenCanonical = "{\"components\":[{\"assembly\":\"UnityEngine.CoreModule\",\"index\":0,\"owner\":[],\"properties\":{\"m_Children\":[],\"m_ConstrainProportionsScale\":false,\"m_Father\":null,\"m_GameObject\":{\"internal\":{\"path\":[]}},\"m_LocalEulerAnglesHint\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalPosition\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalRotation\":{\"w\":1e0,\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalScale\":{\"x\":1e0,\"y\":1e0,\"z\":1e0}},\"type\":\"UnityEngine.Transform\"}],\"hierarchy\":[{\"name\":\"golden\",\"path\":[],\"properties\":{\"m_Component\":[{\"component\":{\"internal\":{\"component\":0,\"path\":[]}}}],\"m_Icon\":null,\"m_IsActive\":true,\"m_Layer\":0,\"m_Name\":\"golden\",\"m_NavMeshLayer\":0,\"m_StaticEditorFlags\":0,\"m_TagString\":\"Untagged\"}}],\"overrides\":[],\"prefabFingerprintVersion\":1,\"references\":[]}";

        private const string GoldenHash = "25367679a4be755b89b65ff6d661b41364fc0e79ab7886d28da50a4204633eae";

        // The hand-written inner prefab for the override golden: one
        // GameObject + Transform on fixed fileIDs (100 / 400), imported
        // beside a .meta that pins GoldenInnerGuid so every external
        // target/reference in the document is fixed.
        private const string GoldenInnerYaml =
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
            "  m_Name: goldeninner\n" +
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

        private const string GoldenInnerMeta =
            "fileFormatVersion: 2\n" +
            "guid: " + GoldenInnerGuid + "\n" +
            "PrefabImporter:\n" +
            "  externalObjects: {}\n" +
            "  userData: \n" +
            "  assetBundleName: \n" +
            "  assetBundleVariant: \n";

        // SG-12 pinned v1 document for the override state: outer root
        // "goldenovr" + a nested instance of goldeninner.prefab renamed to
        // "FpGoldenInstance". The instance modification set (m_Name plus
        // the transform defaults) lands in overrides; the pinned source
        // objects land in references. Recorded on Unity 6000.0 and
        // byte-identical on Unity 6000.5, so both pinned editors share this
        // constant.
        private const string GoldenOverrideCanonical = "{\"components\":[{\"assembly\":\"UnityEngine.CoreModule\",\"index\":0,\"owner\":[],\"properties\":{\"m_Children\":[{\"internal\":{\"component\":0,\"path\":[0]}}],\"m_ConstrainProportionsScale\":false,\"m_Father\":null,\"m_GameObject\":{\"internal\":{\"path\":[]}},\"m_LocalEulerAnglesHint\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalPosition\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalRotation\":{\"w\":1e0,\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalScale\":{\"x\":1e0,\"y\":1e0,\"z\":1e0}},\"type\":\"UnityEngine.Transform\"},{\"assembly\":\"UnityEngine.CoreModule\",\"index\":0,\"owner\":[0],\"properties\":{\"m_Children\":[],\"m_ConstrainProportionsScale\":false,\"m_Father\":{\"internal\":{\"component\":0,\"path\":[]}},\"m_GameObject\":{\"internal\":{\"path\":[0]}},\"m_LocalEulerAnglesHint\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalPosition\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalRotation\":{\"w\":1e0,\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalScale\":{\"x\":1e0,\"y\":1e0,\"z\":1e0}},\"type\":\"UnityEngine.Transform\"}],\"hierarchy\":[{\"name\":\"goldenovr\",\"path\":[],\"properties\":{\"m_Component\":[{\"component\":{\"internal\":{\"component\":0,\"path\":[]}}}],\"m_Icon\":null,\"m_IsActive\":true,\"m_Layer\":0,\"m_Name\":\"goldenovr\",\"m_NavMeshLayer\":0,\"m_StaticEditorFlags\":0,\"m_TagString\":\"Untagged\"}},{\"name\":\"FpGoldenInstance\",\"path\":[0],\"prefab\":{\"assetType\":\"Regular\",\"source\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":100}},\"properties\":{\"m_Component\":[{\"component\":{\"internal\":{\"component\":0,\"path\":[0]}}}],\"m_Icon\":null,\"m_IsActive\":true,\"m_Layer\":0,\"m_Name\":\"FpGoldenInstance\",\"m_NavMeshLayer\":0,\"m_StaticEditorFlags\":0,\"m_TagString\":\"Untagged\"}}],\"overrides\":[{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_Name\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":100}},\"value\":\"FpGoldenInstance\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalEulerAnglesHint.x\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"0\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalEulerAnglesHint.y\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"0\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalEulerAnglesHint.z\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"0\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalPosition.x\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"0\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalPosition.y\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"0\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalPosition.z\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"0\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalRotation.w\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"1\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalRotation.x\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"0\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalRotation.y\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"0\"},{\"instance\":[0],\"kind\":\"property\",\"objectReference\":null,\"propertyPath\":\"m_LocalRotation.z\",\"target\":{\"external\":{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}},\"value\":\"0\"}],\"prefabFingerprintVersion\":1,\"references\":[{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":100},{\"guid\":\"3a8f1c4e7b2d49568e0f1a3b5c7d9e2f\",\"localFileId\":400}]}";

        private const string GoldenOverrideHash = "386cd689a4c859d0f69069a1e72b9510b2c864a8ecd50adfc64daffa80cbff86";

        // SG-12 pinned v1 document for the external-reference state:
        // root "goldenext" + Transform + FingerprintFixtureBehaviour
        // whose linkedAsset and m_Script reference the fixture
        // MonoScript (guid 8c4f2a1d…, localFileId 11500000),
        // deduplicated into a single references entry. Unity 6000.0 pin —
        // on Unity 6000.5 the same fixture state persists a non-empty
        // m_EditorClassIdentifier, so that editor uses
        // GoldenExternalCanonical6000_5 instead.
        private const string GoldenExternalCanonical = "{\"components\":[{\"assembly\":\"UnityEngine.CoreModule\",\"index\":0,\"owner\":[],\"properties\":{\"m_Children\":[],\"m_ConstrainProportionsScale\":false,\"m_Father\":null,\"m_GameObject\":{\"internal\":{\"path\":[]}},\"m_LocalEulerAnglesHint\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalPosition\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalRotation\":{\"w\":1e0,\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalScale\":{\"x\":1e0,\"y\":1e0,\"z\":1e0}},\"type\":\"UnityEngine.Transform\"},{\"assembly\":\"OpWeaveFixtures.Runtime\",\"index\":1,\"owner\":[],\"properties\":{\"amount\":0e0,\"enabled_flag\":false,\"label\":\"\",\"linkedAsset\":{\"external\":{\"guid\":\"8c4f2a1d9e5b47a6b3d0e2f1c6a85b97\",\"localFileId\":11500000}},\"linkedObject\":null,\"m_EditorClassIdentifier\":\"\",\"m_EditorHideFlags\":0,\"m_Enabled\":true,\"m_GameObject\":{\"internal\":{\"path\":[]}},\"m_Name\":\"\",\"m_Script\":{\"external\":{\"guid\":\"8c4f2a1d9e5b47a6b3d0e2f1c6a85b97\",\"localFileId\":11500000}},\"managed\":null,\"nested\":[],\"number\":0,\"numbers\":[],\"offset\":{\"x\":0e0,\"y\":0e0,\"z\":0e0}},\"type\":\"OpWeave.Fixtures.FingerprintFixtureBehaviour\"}],\"hierarchy\":[{\"name\":\"goldenext\",\"path\":[],\"properties\":{\"m_Component\":[{\"component\":{\"internal\":{\"component\":0,\"path\":[]}}},{\"component\":{\"internal\":{\"component\":1,\"path\":[]}}}],\"m_Icon\":null,\"m_IsActive\":true,\"m_Layer\":0,\"m_Name\":\"goldenext\",\"m_NavMeshLayer\":0,\"m_StaticEditorFlags\":0,\"m_TagString\":\"Untagged\"}}],\"overrides\":[],\"prefabFingerprintVersion\":1,\"references\":[{\"guid\":\"8c4f2a1d9e5b47a6b3d0e2f1c6a85b97\",\"localFileId\":11500000}]}";

        private const string GoldenExternalHash = "3294f310da16bccff21e1138c050d3c977b7ce5e7d0bf42fa6889c265f678d99";

        // Unity 6000.5's document for the same fixture state, recorded from a
        // head-bound Unity 6000.5.11f1 EditMode run and copied unchanged.
        // Compared with the Unity 6000.0 document above it differs in exactly
        // one leaf — components/1/properties/m_EditorClassIdentifier, which
        // 6000.5 persists as the fixture type name and 6000.0 persists empty —
        // so both are real persisted content and both are pinned.
        private const string GoldenExternalCanonical6000_5 = "{\"components\":[{\"assembly\":\"UnityEngine.CoreModule\",\"index\":0,\"owner\":[],\"properties\":{\"m_Children\":[],\"m_ConstrainProportionsScale\":false,\"m_Father\":null,\"m_GameObject\":{\"internal\":{\"path\":[]}},\"m_LocalEulerAnglesHint\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalPosition\":{\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalRotation\":{\"w\":1e0,\"x\":0e0,\"y\":0e0,\"z\":0e0},\"m_LocalScale\":{\"x\":1e0,\"y\":1e0,\"z\":1e0}},\"type\":\"UnityEngine.Transform\"},{\"assembly\":\"OpWeaveFixtures.Runtime\",\"index\":1,\"owner\":[],\"properties\":{\"amount\":0e0,\"enabled_flag\":false,\"label\":\"\",\"linkedAsset\":{\"external\":{\"guid\":\"8c4f2a1d9e5b47a6b3d0e2f1c6a85b97\",\"localFileId\":11500000}},\"linkedObject\":null,\"m_EditorClassIdentifier\":\"OpWeaveFixtures.Runtime::OpWeave.Fixtures.FingerprintFixtureBehaviour\",\"m_EditorHideFlags\":0,\"m_Enabled\":true,\"m_GameObject\":{\"internal\":{\"path\":[]}},\"m_Name\":\"\",\"m_Script\":{\"external\":{\"guid\":\"8c4f2a1d9e5b47a6b3d0e2f1c6a85b97\",\"localFileId\":11500000}},\"managed\":null,\"nested\":[],\"number\":0,\"numbers\":[],\"offset\":{\"x\":0e0,\"y\":0e0,\"z\":0e0}},\"type\":\"OpWeave.Fixtures.FingerprintFixtureBehaviour\"}],\"hierarchy\":[{\"name\":\"goldenext\",\"path\":[],\"properties\":{\"m_Component\":[{\"component\":{\"internal\":{\"component\":0,\"path\":[]}}},{\"component\":{\"internal\":{\"component\":1,\"path\":[]}}}],\"m_Icon\":null,\"m_IsActive\":true,\"m_Layer\":0,\"m_Name\":\"goldenext\",\"m_NavMeshLayer\":0,\"m_StaticEditorFlags\":0,\"m_TagString\":\"Untagged\"}}],\"overrides\":[],\"prefabFingerprintVersion\":1,\"references\":[{\"guid\":\"8c4f2a1d9e5b47a6b3d0e2f1c6a85b97\",\"localFileId\":11500000}]}";

        private const string GoldenExternalHash6000_5 = "40c32436951641d25cfc73749f87c7dd229640de6d08c7869c8a9e5530e2ee52";
    }
}
