#nullable enable

// T-W2.6-01 [Edit] test (local:T-W2.6-01:prefab-fingerprint, SPEC §T.4
// L-15): the PrefabContentFingerprint v1 of every proven state — normal,
// Nested, Variant, Override, Managed Reference, arrays, external
// references — computed on the to-be-saved content must equal the
// fingerprint of the saved asset after a real domain reload
// (RecompileScripts, SPEC §T.3). A failed Variant comparison
// Debug.Logs both Canonical JSON documents on single
// [FP-VARIANT-PRE] / [FP-VARIANT-RELOAD] lines for the next round's
// diff. Fields do not survive the reload, so all state passes through
// <project>/Library/OpWeaveFingerprintProbe/.
//
// Root-name rule (local, documented): Unity names the saved Prefab root
// after the asset file, so the pre-save fingerprint is computed through
// ComputeForSave(root, destinationPath), which normalizes the root name
// to the destination file name — modelling exactly what Unity persists.
// Each state's pre-save Canonical JSON document is persisted next to
// state.txt so a mismatch can print both documents.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class PrefabFingerprintReloadTests
    {
        private const string ProbeFolder = "Assets/OpWeaveFingerprintProbe";
        private const string MarkerFolder = "Assets/Editor/OpWeaveFingerprintProbe";
        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveFingerprintProbe\n{\n" +
            "    internal static class ReloadMarker { public const int V = 1; }\n" +
            "}\n";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir => Path.Combine(ProjectRoot, "Library", "OpWeaveFingerprintProbe");

        private static string StatePath => Path.Combine(ProbeDir, "state.txt");

        private static string PreDocPath(string state) =>
            Path.Combine(ProbeDir, state + ".pre.json");

        private static string VerdictPath => Path.Combine(ProbeDir, "l15-verdicts.txt");

        private static string MarkerDisk =>
            Path.Combine(ProjectRoot, MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private static object? s_sentinel;
        private static void ArmSentinel() => s_sentinel = new object();
        private static bool SentinelAlive => s_sentinel is not null;

        private static void Cleanup()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            if (AssetDatabase.IsValidFolder(MarkerFolder))
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
            }
            if (Directory.Exists(MarkerDisk))
            {
                Directory.Delete(MarkerDisk, recursive: true);
            }
            AssetDatabase.Refresh();
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var markerWasPresent = AssetDatabase.IsValidFolder(MarkerFolder) ||
                Directory.Exists(MarkerDisk);
            Cleanup();
            if (markerWasPresent)
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var markerWasPresent = AssetDatabase.IsValidFolder(MarkerFolder) ||
                Directory.Exists(MarkerDisk);
            Cleanup();
            if (markerWasPresent)
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator Fingerprints_EqualAcrossDomainReload_AllStates()
        {
            var phase1 = BuildFixturesAndRecord();
            Assert.That(phase1.Ok, Is.True, "pre-reload phase failed: " + phase1.Failure);

            yield return new RecompileScripts();
            var guard = 0;
            while (SentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(SentinelAlive, "the probe compile did not produce a domain reload");

            var phase2 = RecomputeAndCompare();
            Assert.That(phase2.Ok, Is.True, "post-reload phase failed: " + phase2.Failure);
        }

        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;

            public static PhaseResult Pass() => new PhaseResult { Ok = true };

            public static PhaseResult Fail(string m) =>
                new PhaseResult { Failure = m };
        }

        private static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveFingerprintProbe");
            }
        }

        private static GameObject Save(GameObject go, string path)
        {
            var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
            if (saved == null)
            {
                throw new InvalidOperationException("fixture did not save: " + path);
            }
            return saved;
        }

        // Computes the destination-aware Preflight fingerprint on the
        // to-be-saved content — the "before saving" read of
        // local:T-W2.6-01 (SPEC §G6.1).
        private static string? PreflightFingerprint(
            GameObject content, string path, out string hash, out string canonical)
        {
            var result = PrefabContentFingerprint.ComputeForSave(content, path);
            if (result is not PrefabFingerprintResult.Computed computed)
            {
                hash = string.Empty;
                canonical = string.Empty;
                return "fingerprint did not compute: " +
                    ((PrefabFingerprintResult.Unknown)result).Reason;
            }
            hash = computed.Fingerprint.Hash;
            canonical = computed.Fingerprint.Document.CanonicalText;
            return null;
        }

        private static string? RecoveryFingerprint(
            string path, out string hash, out string canonical)
        {
            var result = PrefabContentFingerprint.ComputeAsset(path);
            if (result is not PrefabFingerprintResult.Computed computed)
            {
                hash = string.Empty;
                canonical = string.Empty;
                return "fingerprint did not compute: " +
                    ((PrefabFingerprintResult.Unknown)result).Reason;
            }
            hash = computed.Fingerprint.Hash;
            canonical = computed.Fingerprint.Document.CanonicalText;
            return null;
        }

        // Builds one fixture state, records its pre-save fingerprint and
        // Canonical JSON document, and saves it; every state line is
        // "name|path|preflightHash" and the document sits in
        // <name>.pre.json so a mismatch prints both documents.
        private static string? RecordState(
            List<string> states, string name, GameObject toBeSaved, string path)
        {
            var error = PreflightFingerprint(toBeSaved, path, out var hash, out var canonical);
            if (error is not null)
            {
                return name + " pre-save fingerprint: " + error;
            }
            Save(toBeSaved, path);
            states.Add(name + "|" + path + "|" + hash);
            File.WriteAllText(PreDocPath(name), canonical);
            return null;
        }

        private static PhaseResult BuildFixturesAndRecord()
        {
            try
            {
                EnsureFolder();
                Directory.CreateDirectory(ProbeDir);
                var states = new List<string>();
                var created = new List<GameObject>();
                try
                {
                    // normal
                    var normal = new GameObject("normal");
                    created.Add(normal);
                    var normalComp = FingerprintFixtureAccess.AddBehaviour(normal);
                    FingerprintFixtureAccess.Edit(normalComp,
                        so => so.FindProperty("number").intValue = 7);
                    var error = RecordState(states, "normal", normal,
                        ProbeFolder + "/normal.prefab");
                    if (error is not null)
                    {
                        return PhaseResult.Fail(error);
                    }

                    // nested
                    var inner = new GameObject("inner");
                    created.Add(inner);
                    var innerComp = FingerprintFixtureAccess.AddBehaviour(inner);
                    FingerprintFixtureAccess.Edit(innerComp,
                        so => so.FindProperty("number").intValue = 3);
                    error = RecordState(states, "inner", inner,
                        ProbeFolder + "/inner.prefab");
                    if (error is not null)
                    {
                        return PhaseResult.Fail(error);
                    }
                    var outer = new GameObject("nested");
                    created.Add(outer);
                    var innerAsset = AssetDatabase.LoadAssetAtPath<GameObject>(
                        ProbeFolder + "/inner.prefab");
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset)!;
                    created.Add(inst);
                    inst.transform.SetParent(outer.transform, worldPositionStays: false);
                    error = RecordState(states, "nested", outer,
                        ProbeFolder + "/nested.prefab");
                    if (error is not null)
                    {
                        return PhaseResult.Fail(error);
                    }

                    // variant — the content root is itself an instance
                    // of its base; the persisted source reference plus
                    // override set must fingerprint identically across
                    // the reload like every proven state (file header)
                    var baseGo = new GameObject("base");
                    created.Add(baseGo);
                    var baseComp = FingerprintFixtureAccess.AddBehaviour(baseGo);
                    FingerprintFixtureAccess.Edit(baseComp,
                        so => so.FindProperty("number").intValue = 9);
                    error = RecordState(states, "base", baseGo,
                        ProbeFolder + "/base.prefab");
                    if (error is not null)
                    {
                        return PhaseResult.Fail(error);
                    }
                    var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(
                        ProbeFolder + "/base.prefab");
                    var vinst = (GameObject)PrefabUtility.InstantiatePrefab(baseAsset)!;
                    created.Add(vinst);
                    vinst.name = "variant";
                    error = RecordState(states, "variant", vinst,
                        ProbeFolder + "/variant.prefab");
                    if (error is not null)
                    {
                        return PhaseResult.Fail(error);
                    }

                    // override (property modification + added component on a
                    // nested instance, then renamed — recorded overrides)
                    var ovOuter = new GameObject("override");
                    created.Add(ovOuter);
                    var ovInst = (GameObject)PrefabUtility.InstantiatePrefab(innerAsset)!;
                    created.Add(ovInst);
                    ovInst.transform.SetParent(ovOuter.transform, worldPositionStays: false);
                    ovInst.name = "FpRenamedInstance";
                    var ovComp = FingerprintFixtureAccess.AddBehaviour(ovInst);
                    FingerprintFixtureAccess.Edit(ovComp,
                        so => so.FindProperty("number").intValue = 21);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(ovInst);
                    error = RecordState(states, "override", ovOuter,
                        ProbeFolder + "/override.prefab");
                    if (error is not null)
                    {
                        return PhaseResult.Fail(error);
                    }

                    // managed reference
                    var managed = new GameObject("managed");
                    created.Add(managed);
                    var managedComp = FingerprintFixtureAccess.AddBehaviour(managed);
                    FingerprintFixtureAccess.Edit(managedComp, so =>
                    {
                        so.FindProperty("managed").managedReferenceValue =
                            FingerprintFixtureAccess.NewManagedImpl(4, 0.75f);
                    });
                    error = RecordState(states, "managedReference", managed,
                        ProbeFolder + "/managed.prefab");
                    if (error is not null)
                    {
                        return PhaseResult.Fail(error);
                    }

                    // arrays
                    var arrays = new GameObject("arrays");
                    created.Add(arrays);
                    var arraysComp = FingerprintFixtureAccess.AddBehaviour(arrays);
                    FingerprintFixtureAccess.Edit(arraysComp, so =>
                    {
                        var numbers = so.FindProperty("numbers");
                        numbers.arraySize = 3;
                        numbers.GetArrayElementAtIndex(0).intValue = 1;
                        numbers.GetArrayElementAtIndex(1).intValue = 2;
                        numbers.GetArrayElementAtIndex(2).intValue = 3;
                        var nestedProp = so.FindProperty("nested");
                        nestedProp.arraySize = 1;
                        var element = nestedProp.GetArrayElementAtIndex(0);
                        element.FindPropertyRelative("amount").floatValue = 1.5f;
                        element.FindPropertyRelative("note").stringValue = "a";
                    });
                    error = RecordState(states, "arrays", arrays,
                        ProbeFolder + "/arrays.prefab");
                    if (error is not null)
                    {
                        return PhaseResult.Fail(error);
                    }

                    // external references
                    var linked = FingerprintFixtureAccess.NewLinkedAsset();
                    AssetDatabase.CreateAsset(linked, ProbeFolder + "/linked.asset");
                    var linkedPrefab = Save(
                        new GameObject("linkedprefab"), ProbeFolder + "/linkedprefab.prefab");
                    var external = new GameObject("external");
                    created.Add(external);
                    var extComp = FingerprintFixtureAccess.AddBehaviour(external);
                    FingerprintFixtureAccess.Edit(extComp, so =>
                    {
                        so.FindProperty("linkedAsset").objectReferenceValue = linked;
                        so.FindProperty("linkedObject").objectReferenceValue = linkedPrefab;
                    });
                    error = RecordState(states, "externalReferences", external,
                        ProbeFolder + "/external.prefab");
                    if (error is not null)
                    {
                        return PhaseResult.Fail(error);
                    }
                }
                finally
                {
                    foreach (var go in created)
                    {
                        if (go != null)
                        {
                            UnityEngine.Object.DestroyImmediate(go);
                        }
                    }
                }

                Directory.CreateDirectory(ProbeDir);
                File.WriteAllText(StatePath, string.Join("\n", states) + "\n");

                Directory.CreateDirectory(MarkerDisk);
                File.WriteAllText(
                    Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
                AssetDatabase.Refresh();
                ArmSentinel();
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-1 threw: " + e);
            }
        }

        private static PhaseResult RecomputeAndCompare()
        {
            try
            {
                if (!File.Exists(StatePath))
                {
                    return PhaseResult.Fail("the persisted probe state is missing");
                }
                var verdicts = new StringBuilder();
                var failures = new List<string>();
                foreach (var line in File.ReadAllLines(StatePath))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 3)
                    {
                        continue;
                    }
                    var state = parts[0];
                    var path = parts[1];
                    var expected = parts[2];
                    var error = RecoveryFingerprint(path, out var hash, out var canonical);
                    if (error is not null)
                    {
                        failures.Add(state + ": " + error);
                        verdicts.Append(state).Append(": UNKNOWN(").Append(error).Append(")\n");
                        continue;
                    }
                    if (hash != expected)
                    {
                        var preDoc = File.Exists(PreDocPath(state))
                            ? File.ReadAllText(PreDocPath(state))
                            : "<the persisted pre-save document is missing>";
                        if (state == "variant")
                        {
                            UnityEngine.Debug.Log("[FP-VARIANT-PRE]" + preDoc);
                            UnityEngine.Debug.Log("[FP-VARIANT-RELOAD]" + canonical);
                        }
                        failures.Add(state + ": fingerprint differs across the domain reload" +
                            "\n--- pre-save document ---\n" + preDoc +
                            "\n--- post-reload document ---\n" + canonical);
                        verdicts.Append(state).Append(": DIFF\n");
                        continue;
                    }
                    verdicts.Append(state).Append(": EQUAL\n");
                }
                File.WriteAllText(VerdictPath, verdicts.ToString());
                UnityEngine.Debug.Log("[L-15] " + verdicts.ToString().Replace("\n", " "));
                return failures.Count == 0
                    ? PhaseResult.Pass()
                    : PhaseResult.Fail(string.Join("; ", failures));
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-2 threw: " + e);
            }
        }
    }
}
