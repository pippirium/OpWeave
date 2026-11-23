#nullable enable

// T-W2.7-03 [Edit] tests: local:T-W2.7-03:asset-create-ops and
// local:T-W2.7-03:asset-create-reconcile for unity.asset.create_material and
// unity.asset.create_scriptable_object (SPEC §I.3, §G7 items 4 and 7, §F5).
//
// Coverage:
//  - create_material: a Material is created with the requested shader and
//    each §G7 item 4 property kind (float, Color by object and hex string,
//    Vector4, Texture AssetRef); missing shader → OW-UNITY-006; existing
//    path → OW-UNITY-005; malformed property → OW-OP-006.
//  - create_scriptable_object: an asset of the named type is created with
//    the declared values applied to its serialized fields; missing or
//    non-creatable type → OW-UNITY-006; existing path → OW-UNITY-005;
//    unapplyable values → OW-UNITY-007.
//  - The six §F5 Operation-level cases at Reconcile for each Operation:
//    not executed; executed but StepSucceeded not recorded; partial side
//    effect; pre-existing same-name artifact; artifact modified by another
//    operation; simulated Editor restart (different editorSessionId — the
//    file artifact stays re-identifiable by its permanent path/guid keys).
//  - One [UnityTest] per Operation crosses a real domain reload (SPEC §T.3):
//    Reconcile on the persisted Preflight snapshot returns Completed with
//    the uninterrupted outputs.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Behaviour and §F5 reconciliation of <c>unity.asset.create_material</c>
    /// and <c>unity.asset.create_scriptable_object</c>.
    /// </summary>
    internal sealed class AssetCreateOperationTests
    {
        private const string ProbeFolder = "Assets/OpWeaveAssetCreate";
        private const string MarkerFolder = "Assets/Editor/OpWeaveAssetCreateProbe";
        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveAssetCreateProbe\n{\n" +
            "    internal static class ReloadMarker { public const int V = 1; }\n" +
            "}\n";

        // A real Shader asset declaring every §G7 item 4 property kind —
        // the positive fixture: Execute applies only names the shader
        // declares with a compatible type.
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

        private static readonly IReadOnlyDictionary<string, string> MaterialInputs =
            new Dictionary<string, string>
            {
                ["path"] = "unity.AssetPath",
                ["shader"] = "core.String",
                ["properties"] = "core.Json",
            };

        private static readonly IReadOnlyDictionary<string, string> MaterialOutputs =
            new Dictionary<string, string>
            {
                ["material"] = "unity.AssetRef<unity.Material>",
            };

        private static readonly IReadOnlyDictionary<string, string> ObjectInputs =
            new Dictionary<string, string>
            {
                ["path"] = "unity.AssetPath",
                ["type"] = "unity.TypeName",
                ["values"] = "core.Json",
            };

        private static readonly IReadOnlyDictionary<string, string> ObjectOutputs =
            new Dictionary<string, string>
            {
                ["asset"] = "unity.AssetRef<unity.ScriptableObject>",
            };

        [SetUp]
        public void SetUp() => EnsureFolder(ProbeFolder);

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            var disk = PhysicalPath(ProbeFolder);
            if (Directory.Exists(disk))
            {
                try { Directory.Delete(disk, recursive: true); } catch (Exception) { }
            }
            if (File.Exists(disk + ".meta"))
            {
                try { File.Delete(disk + ".meta"); } catch (Exception) { }
            }
            if (Directory.Exists(ProbeStateDir))
            {
                try { Directory.Delete(ProbeStateDir, recursive: true); } catch (Exception) { }
            }
            AssetDatabase.Refresh();
        }

        // The domain-reload [UnityTest]s compile a temporary marker script
        // so the RecompileScripts yield really reloads the domain (SPEC
        // §T.3). Whenever a leftover marker is found — from an aborted
        // earlier run — remove it and recompile once so the next test
        // starts from a clean project, the PrefabFingerprintReloadTests
        // pattern.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            var markerWasPresent = AssetDatabase.IsValidFolder(MarkerFolder) ||
                Directory.Exists(MarkerDisk);
            CleanupMarker();
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
            CleanupMarker();
            if (markerWasPresent)
            {
                yield return new RecompileScripts(false);
            }
        }

        private static string MarkerDisk =>
            Path.Combine(ProjectRoot(), MarkerFolder.Replace('/', Path.DirectorySeparatorChar));

        private static void CleanupMarker()
        {
            if (AssetDatabase.IsValidFolder(MarkerFolder))
            {
                AssetDatabase.DeleteAsset(MarkerFolder);
            }
            if (Directory.Exists(MarkerDisk))
            {
                try { Directory.Delete(MarkerDisk, recursive: true); } catch (Exception) { }
            }
            if (File.Exists(MarkerDisk + ".meta"))
            {
                try { File.Delete(MarkerDisk + ".meta"); } catch (Exception) { }
            }
            AssetDatabase.Refresh();
        }

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeStateDir =>
            Path.Combine(ProjectRoot(), "Library", "OpWeaveAssetCreateProbe");

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));

        // ══ unity.asset.create_material — Execute ═══════════════════════

        [Test]
        public void Material_Execute_CreatesWithShaderAndEveryPropertyKind()
        {
            var shaderName = CreateProbeShader();
            var texturePath = CreateProbeTexture();
            var textureGuid = AssetDatabase.AssetPathToGUID(texturePath);
            var path = ProbeFolder + "/ProbeMat.mat";
            var changes = new StepChangeRecorder(0, "mat");
            var context = MaterialContext(
                path, shaderName,
                "{ \"_Glossiness\": 0.5," +
                "  \"_Metallic\": 1," +
                "  \"_Color\": {\"r\": 1, \"g\": 0.2, \"b\": 0.2, \"a\": 1}," +
                "  \"_EmissionColor\": \"#10203040\"," +
                "  \"_Offset\": {\"x\": 1, \"y\": 2.5, \"z\": 3, \"w\": 4}," +
                "  \"_MainTex\": {\"kind\": \"asset\", \"guid\": \"" + textureGuid +
                    "\", \"path\": \"" + texturePath + "\"} }",
                changes);

            var result = new CreateMaterialOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            Assert.That(material, Is.Not.Null, "the step must create the .mat asset");
            Assert.That(material!.shader.name, Is.EqualTo(ProbeShaderName));
            Assert.That(material.GetFloat("_Glossiness"), Is.EqualTo(0.5f));
            Assert.That(material.GetFloat("_Metallic"), Is.EqualTo(1f),
                "a JSON integer component applies through the float path");
            var color = material.GetColor("_Color");
            Assert.That(color.r, Is.EqualTo(1f));
            Assert.That(color.g, Is.EqualTo(0.2f));
            Assert.That(color.b, Is.EqualTo(0.2f));
            Assert.That(color.a, Is.EqualTo(1f));
            var emission = material.GetColor("_EmissionColor");
            Assert.That(emission.r, Is.EqualTo(0x10 / 255f).Within(0.001f));
            Assert.That(emission.a, Is.EqualTo(0x40 / 255f).Within(0.001f));
            Assert.That(material.GetVector("_Offset"), Is.EqualTo(new Vector4(1, 2.5f, 3, 4)),
                "integer and float vector members both apply");
            Assert.That(material.GetTexture("_MainTex"), Is.EqualTo(
                AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath)));

            var output = (JObject)context.Outputs["material"].ToToken();
            Assert.That((string?)output["kind"], Is.EqualTo("asset"));
            Assert.That((string?)output["path"], Is.EqualTo(path));
            Assert.That((string?)output["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets)));
            AssertOutputsMatchDeclaredPortTypes(context, MaterialOutputs);

            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Created));
            Assert.That(changes.Records[0].Target.Kind, Is.EqualTo("asset"));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(path));
            Assert.That(changes.Records[0].Undoable, Is.False);
        }

        [Test]
        public void Material_Execute_MissingShader_Fails_OwUnity006()
        {
            var result = new CreateMaterialOperation().Execute(
                MaterialContext(ProbeFolder + "/NoShader.mat", "No Such Shader Name 9000"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_006, "shader");
            Assert.That(File.Exists(PhysicalPath(ProbeFolder + "/NoShader.mat")), Is.False,
                "a refused create must not leave a file behind");

            var preflight = new CreateMaterialOperation().Preflight(
                MaterialContext(ProbeFolder + "/NoShader.mat", "No Such Shader Name 9000"));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_006),
                "Preflight reports the missing shader too (Dry Run truth)");
        }

        [Test]
        public void Material_Execute_ExistingPath_Fails_OwUnity005_AndLeavesItAlone()
        {
            var path = ProbeFolder + "/Taken.mat";
            CreateProbeMaterial(path, "Standard");

            var result = new CreateMaterialOperation().Execute(
                MaterialContext(path, "Standard"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
            Assert.That(
                AssetDatabase.LoadAssetAtPath<Material>(path)!.shader.name,
                Is.EqualTo("Standard"),
                "the existing artifact is never overwritten");

            var preflight = new CreateMaterialOperation().Preflight(MaterialContext(path, "Standard"));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_005),
                "Preflight reports the occupied target too (Dry Run truth)");
        }

        [Test]
        public void Material_Execute_EscapedPath_Fails_OwPerm002()
        {
            var result = new CreateMaterialOperation().Execute(
                MaterialContext("Assets/../OpWeaveAssetCreateEscape/Esc.mat", "Standard"));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "path");
        }

        [Test]
        public void Material_Execute_NonMatExtension_Fails_OwUnity005()
        {
            var result = new CreateMaterialOperation().Execute(
                MaterialContext(ProbeFolder + "/WrongExt.asset", "Standard"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
        }

        [Test]
        public void Material_Execute_MissingFolder_Fails_OwUnity005()
        {
            var result = new CreateMaterialOperation().Execute(
                MaterialContext("Assets/OpWeaveAssetCreateNoSuchDir/Orphan.mat", "Standard"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
        }

        [TestCase("{ \"_X\": true }", TestName = "Material_Execute_BoolProperty_Rejected")]
        [TestCase("{ \"_X\": [1, 2] }", TestName = "Material_Execute_ArrayProperty_Rejected")]
        [TestCase("{ \"_X\": {\"p\": 1} }", TestName = "Material_Execute_OpaqueProperty_Rejected")]
        [TestCase("42", TestName = "Material_Execute_NonObjectProperties_Rejected")]
        public void Material_Execute_InvalidPropertyShape_Fails_OwOp006(string properties)
        {
            var result = new CreateMaterialOperation().Execute(
                MaterialContext(ProbeFolder + "/BadProps.mat", "Standard", properties));

            AssertFailedWith(result, ErrorCodes.OW_OP_006, "properties");
            Assert.That(File.Exists(PhysicalPath(ProbeFolder + "/BadProps.mat")), Is.False,
                "a refused properties map must not leave a file behind");
        }

        // A well-formed value for a name the shader does not declare can
        // never be applied: the step fails OW-UNITY-007 instead of
        // reporting success over a material that does not carry it.
        [TestCase("{ \"_NoSuchScalar\": 0.5 }",
            TestName = "Material_Execute_UndeclaredFloat_Fails_OwUnity007")]
        [TestCase("{ \"_Offset\": {\"x\": 1, \"y\": 2, \"z\": 3, \"w\": 4} }",
            TestName = "Material_Execute_UndeclaredVector_Fails_OwUnity007")]
        public void Material_Execute_UndeclaredProperty_Fails_OwUnity007(string properties)
        {
            var result = new CreateMaterialOperation().Execute(
                MaterialContext(ProbeFolder + "/Undeclared.mat", "Standard", properties));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_007, "properties");
            Assert.That(File.Exists(PhysicalPath(ProbeFolder + "/Undeclared.mat")), Is.False,
                "a refused properties map must not leave a file behind");

            var preflight = new CreateMaterialOperation().Preflight(
                MaterialContext(ProbeFolder + "/Undeclared.mat", "Standard", properties));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_007),
                "Preflight reports the undeclared property too (Dry Run truth)");
        }

        [Test]
        public void Material_Execute_UnresolvableTextureRef_Fails_OwUnity001()
        {
            var result = new CreateMaterialOperation().Execute(
                MaterialContext(
                    ProbeFolder + "/BadTex.mat", "Standard",
                    "{ \"_MainTex\": {\"kind\": \"asset\", \"path\": \"Assets/NoTex.png\"} }"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_001, null);
        }

        // The §F5 evidence row: normalized path, nothing-there, shader and
        // the declared properties — plus the session id.
        [Test]
        public void Material_Preflight_Snapshot_CarriesTheEvidenceRow()
        {
            var preflight = new CreateMaterialOperation().Preflight(
                MaterialContext(
                    ProbeFolder + "/Snap.mat", "Standard",
                    "{ \"_Glossiness\": 0.25 }"));

            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((string?)snapshot["path"], Is.EqualTo(ProbeFolder + "/Snap.mat"));
            Assert.That((bool?)snapshot["pathExisted"], Is.False);
            Assert.That((string?)snapshot["shader"], Is.EqualTo("Standard"));
            Assert.That((double?)((JObject)snapshot["properties"]!)["_Glossiness"], Is.EqualTo(0.25));
            Assert.That((string?)snapshot["editorSessionId"], Is.Not.Empty);
            Assert.That(preflight.PlannedChanges, Is.Empty);
        }

        [Test]
        public void Material_Preflight_SkipsPendingInputs()
        {
            var context = MaterialContext(
                ProbeFolder + "/Pending.mat", "Standard",
                pending: new Dictionary<string, string> { ["path"] = "producer" },
                present: false);

            var preflight = new CreateMaterialOperation().Preflight(context);

            Assert.That(preflight.Diagnostics, Is.Empty);
        }

        // ══ unity.asset.create_material — §F5 Reconcile, six cases ══════

        // Case 1 — interrupted before Execute: nothing at the target is
        // provably not applied, and re-execution is safe.
        [Test]
        public void Material_Reconcile_NotExecuted_ReturnsNotApplied()
        {
            var snapshot = MaterialSnapshot(ProbeFolder + "/RecNew.mat", "Standard");

            var verdict = new CreateMaterialOperation().Reconcile(
                MaterialContext(ProbeFolder + "/RecNew.mat", "Standard"), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 2 — executed but StepSucceeded not recorded: the artifact
        // matches every evidence member, so the verdict completes with the
        // uninterrupted run's outputs.
        [Test]
        public void Material_Reconcile_ExecutedUnrecorded_ReturnsCompletedWithOutputs()
        {
            var path = ProbeFolder + "/RecDone.mat";
            var context = MaterialContext(path, "Standard", "{ \"_Glossiness\": 0.75 }");
            var snapshot = new CreateMaterialOperation().Preflight(context).Snapshot;
            Assert.That(
                new CreateMaterialOperation().Execute(MaterialContext(
                        path, "Standard", "{ \"_Glossiness\": 0.75 }")).Kind,
                Is.EqualTo(StepResultKind.Succeeded),
                "the uninterrupted run must have produced the artifact");

            var verdict = new CreateMaterialOperation().Reconcile(context, snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            var material = (JObject)outputs["material"].ToToken();
            Assert.That((string?)material["kind"], Is.EqualTo("asset"));
            Assert.That((string?)material["path"], Is.EqualTo(path));
            Assert.That((string?)material["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets)));
        }

        // Case 3 — partial side effect: a file sits at the path but no
        // Material is imported for it; not a Completed the evidence allows,
        // and the file rules out NotApplied — Unknown, NeedsReview.
        [Test]
        public void Material_Reconcile_PartialSideEffect_ReturnsUnknown()
        {
            var path = ProbeFolder + "/Partial.mat";
            var snapshot = MaterialSnapshot(path, "Standard");
            File.WriteAllText(PhysicalPath(path), "%YAML partial\n");
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path), Is.Null,
                "the partial fixture must stay un-imported for the case to exist");

            var verdict = new CreateMaterialOperation().Reconcile(
                MaterialContext(path, "Standard"), snapshot);

            AssertUnknown(verdict, "not a Material");
            Assert.That(File.ReadAllText(PhysicalPath(path)), Is.EqualTo("%YAML partial\n"),
                "an Unknown verdict never deletes the artifact it could not prove");
        }

        // Case 4 — a pre-existing same-name artifact, both recorded
        // variants: the snapshot that itself saw the file, and a foreign
        // artifact that arrived after the empty-path snapshot.
        [Test]
        public void Material_Reconcile_PreexistingArtifact_ReturnsUnknown()
        {
            var path = ProbeFolder + "/PreExisting.mat";
            var context = MaterialContext(path, "Standard", "{ \"_Glossiness\": 0.5 }");
            var snapshot = new CreateMaterialOperation().Preflight(context).Snapshot;
            Assert.That(snapshot, Is.Not.Null);

            // 4a: the snapshot recorded the artifact as pre-existing.
            var seen = (JObject)snapshot.ToToken();
            seen["pathExisted"] = true;
            CreateProbeMaterial(path, "Standard");
            var seenVerdict = new CreateMaterialOperation().Reconcile(
                context, JsonValue.From(seen));
            AssertUnknown(seenVerdict, "pre-existing");

            // 4b: snapshot saw an empty path, then a foreign artifact whose
            // properties do not match the recorded inputs arrived — not the
            // step's output.
            AssetDatabase.DeleteAsset(path);
            var foreign = new Material(Shader.Find("Standard"));
            foreign.SetFloat("_Glossiness", 0.9f);
            AssetDatabase.CreateAsset(foreign, path);
            var foreignVerdict = new CreateMaterialOperation().Reconcile(context, snapshot);
            AssertUnknown(foreignVerdict, "does not match");
        }

        // Case 5 — the artifact was changed by another operation after the
        // step ran: its properties no longer match the expectation.
        [Test]
        public void Material_Reconcile_ModifiedArtifact_ReturnsUnknown_AndDeletesNothing()
        {
            var path = ProbeFolder + "/Modified.mat";
            var context = MaterialContext(path, "Standard", "{ \"_Glossiness\": 0.5 }");
            var snapshot = new CreateMaterialOperation().Preflight(context).Snapshot;
            Assert.That(
                new CreateMaterialOperation().Execute(MaterialContext(
                        path, "Standard", "{ \"_Glossiness\": 0.5 }")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            material!.SetFloat("_Glossiness", 0.99f);
            AssetDatabase.SaveAssetIfDirty(material);

            var verdict = new CreateMaterialOperation().Reconcile(context, snapshot);

            AssertUnknown(verdict, "does not match");
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path), Is.Not.Null,
                "a modified artifact is left untouched");
            Assert.That(
                Directory.GetFiles(PhysicalPath(ProbeFolder), "*.mat"), Has.Length.EqualTo(1),
                "no duplicate artifact was created");
        }

        // Case 6 — a simulated Editor restart (different editorSessionId):
        // the file artifact stays re-identifiable by its permanent keys, so
        // the verdict still follows the evidence (§F5 common rule 3).
        [Test]
        public void Material_Reconcile_SimulatedRestart_StillDecidesByEvidence()
        {
            var path = ProbeFolder + "/Restart.mat";
            var context = MaterialContext(path, "Standard");
            var snapshot = new CreateMaterialOperation().Preflight(context).Snapshot;
            Assert.That(
                new CreateMaterialOperation().Execute(MaterialContext(path, "Standard")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var restarted = (JObject)snapshot.ToToken();
            restarted["editorSessionId"] = "00000000000000000000000000c0ffee";
            var restartSnapshot = JsonValue.From(restarted);

            var verdict = new CreateMaterialOperation().Reconcile(context, restartSnapshot);
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "a file artifact is re-identifiable across a session boundary");

            Assert.That(AssetDatabase.DeleteAsset(path), Is.True,
                "the fixture must delete the imported probe asset");
            AssetDatabase.Refresh();
            Assert.That(File.Exists(PhysicalPath(path)), Is.False);
            Assert.That(File.Exists(PhysicalPath(path) + ".meta"), Is.False);
            var absent = new CreateMaterialOperation().Reconcile(context, restartSnapshot);
            Assert.That(absent, Is.TypeOf<ReconcileResult.NotApplied>(),
                "an absent artifact stays safely re-runnable after a restart");
        }

        // An orphan .meta or a remembered asset entry without a file
        // occupies the path: deliberately Unknown, never NotApplied —
        // re-execution would refuse the occupied target.
        [Test]
        public void Material_Reconcile_OrphanMetaWithoutFile_ReturnsUnknown()
        {
            var path = ProbeFolder + "/OrphanMeta.mat";
            var context = MaterialContext(path, "Standard");
            var snapshot = new CreateMaterialOperation().Preflight(context).Snapshot;
            Assert.That(
                new CreateMaterialOperation().Execute(MaterialContext(path, "Standard")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            File.Delete(PhysicalPath(path));
            Assert.That(File.Exists(PhysicalPath(path)), Is.False);
            Assert.That(File.Exists(PhysicalPath(path) + ".meta"), Is.True,
                "without a refresh the .meta outlives the file");

            var verdict = new CreateMaterialOperation().Reconcile(context, snapshot);
            AssertUnknown(verdict, "without a file");
        }

        // Evidence that cannot be re-established is Unknown — never
        // Completed, never NotApplied.
        [Test]
        public void Material_Reconcile_MalformedOrUnestablishedEvidence_ReturnsUnknown()
        {
            var path = ProbeFolder + "/Evidence.mat";
            var snapshot = MaterialSnapshot(path, "Standard");
            var context = MaterialContext(path, "Standard");

            Assert.That(
                new CreateMaterialOperation().Reconcile(context, JsonValue.From(new JValue(5))),
                Is.TypeOf<ReconcileResult.Unknown>(),
                "a non-object snapshot restores no evidence");

            var missing = (JObject)snapshot.ToToken();
            missing.Remove("shader");
            AssertUnknown(
                new CreateMaterialOperation().Reconcile(context, JsonValue.From(missing)),
                "missing");

            var deadShader = (JObject)snapshot.ToToken();
            deadShader["shader"] = "No Such Shader Name 9000";
            AssertUnknown(
                new CreateMaterialOperation().Reconcile(context, JsonValue.From(deadShader)),
                "shader");
        }

        // ══ unity.asset.create_scriptable_object — Execute ═════════════

        [Test]
        public void Object_Execute_CreatesOfType_WithEveryValueKind()
        {
            var texturePath = CreateProbeTexture();
            var textureGuid = AssetDatabase.AssetPathToGUID(texturePath);
            var path = ProbeFolder + "/Probe.asset";
            var changes = new StepChangeRecorder(0, "so");
            var context = ObjectContext(
                path, typeof(ProbeFieldsAsset).FullName!,
                "{ \"marker\": 42," +
                "  \"small\": 7," +
                "  \"label\": \"probe label\"," +
                "  \"enabled\": true," +
                "  \"scalar\": 2," +
                "  \"tint\": \"#FF8000FF\"," +
                "  \"offset\": {\"x\": 1, \"y\": 2.5, \"z\": 3}," +
                "  \"kind\": \"Beta\"," +
                "  \"surface\": {\"kind\": \"asset\", \"guid\": \"" + textureGuid +
                    "\", \"path\": \"" + texturePath + "\"}," +
                "  \"nested\": {\"number\": 9, \"text\": \"inner\"}," +
                "  \"numbers\": [3, 1, 4] }",
                changes);

            var result = new CreateScriptableObjectOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var asset = AssetDatabase.LoadAssetAtPath<ProbeFieldsAsset>(path);
            Assert.That(asset, Is.Not.Null, "the step must create the .asset file");
            Assert.That(asset!.marker, Is.EqualTo(42));
            Assert.That(asset.small, Is.EqualTo((byte)7));
            Assert.That(asset.label, Is.EqualTo("probe label"));
            Assert.That(asset.enabled, Is.True);
            Assert.That(asset.scalar, Is.EqualTo(2f),
                "a JSON integer applies to a float field");
            Assert.That(asset.tint.r, Is.EqualTo(1f));
            Assert.That(asset.tint.g, Is.EqualTo(0x80 / 255f).Within(0.001f));
            Assert.That(asset.offset, Is.EqualTo(new Vector3(1, 2.5f, 3)),
                "integer and float vector members both apply");
            Assert.That(asset.kind, Is.EqualTo(ProbeFieldsAsset.ProbeKind.Beta));
            Assert.That(asset.surface, Is.EqualTo(
                AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath)));
            Assert.That(asset.nested.number, Is.EqualTo(9));
            Assert.That(asset.nested.text, Is.EqualTo("inner"));
            Assert.That(asset.numbers, Is.EqualTo(new[] { 3, 1, 4 }));

            var output = (JObject)context.Outputs["asset"].ToToken();
            Assert.That((string?)output["kind"], Is.EqualTo("asset"));
            Assert.That((string?)output["path"], Is.EqualTo(path));
            Assert.That((string?)output["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets)));
            AssertOutputsMatchDeclaredPortTypes(context, ObjectOutputs);

            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Created));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(path));
            Assert.That(changes.Records[0].Undoable, Is.False);
        }

        [Test]
        public void Object_Execute_MissingType_Fails_OwUnity006()
        {
            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext(ProbeFolder + "/NoType.asset", "No.Such.TypeName9000"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_006, "type");
        }

        [Test]
        public void Object_Execute_NonScriptableObjectType_Fails_OwUnity006()
        {
            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext(ProbeFolder + "/NotSO.asset", "UnityEngine.Texture2D"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_006, "type");
            Assert.That(File.Exists(PhysicalPath(ProbeFolder + "/NotSO.asset")), Is.False,
                "a refused type must not leave a file behind");
        }

        [Test]
        public void Object_Execute_AbstractType_Fails_OwUnity006()
        {
            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext(
                    ProbeFolder + "/Abstract.asset",
                    "UnityEngine.Rendering.RenderPipelineAsset"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_006, "type");
        }

        [Test]
        public void Object_Execute_ExistingPath_Fails_OwUnity005_AndLeavesItAlone()
        {
            var path = ProbeFolder + "/Taken.asset";
            var probe = ScriptableObject.CreateInstance<ProbeAsset>();
            AssetDatabase.CreateAsset(probe, path);

            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext(path, typeof(ProbeFieldsAsset).FullName!));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
            Assert.That(
                AssetDatabase.LoadAssetAtPath<ProbeAsset>(path), Is.Not.Null,
                "the existing artifact is never overwritten");
        }

        [Test]
        public void Object_Execute_NonAssetExtension_Fails_OwUnity005()
        {
            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext(ProbeFolder + "/WrongExt.mat", typeof(ProbeFieldsAsset).FullName!));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
        }

        [Test]
        public void Object_Execute_EscapedPath_Fails_OwPerm002()
        {
            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext("Assets/../OpWeaveAssetCreateEscape/Esc.asset",
                    typeof(ProbeFieldsAsset).FullName!));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "path");
        }

        [Test]
        public void Object_Execute_UnknownField_Fails_OwUnity007()
        {
            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext(ProbeFolder + "/BadField.asset",
                    typeof(ProbeFieldsAsset).FullName!,
                    "{ \"noSuchField\": 1 }"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_007, "values");
            Assert.That(File.Exists(PhysicalPath(ProbeFolder + "/BadField.asset")), Is.False,
                "a refused values map must not leave a file behind");
        }

        [Test]
        public void Object_Execute_ValueKindMismatch_Fails_OwUnity007()
        {
            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext(ProbeFolder + "/BadValue.asset",
                    typeof(ProbeFieldsAsset).FullName!,
                    "{ \"marker\": \"not a number\" }"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_007, "values");
        }

        [Test]
        public void Object_Execute_IntegerOutOfFieldRange_Fails_OwUnity007()
        {
            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext(ProbeFolder + "/Overflow.asset",
                    typeof(ProbeFieldsAsset).FullName!,
                    "{ \"small\": 300 }"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_007, "values");
        }

        [Test]
        public void Object_Execute_NonObjectValues_Fails_OwOp006()
        {
            var result = new CreateScriptableObjectOperation().Execute(
                ObjectContext(ProbeFolder + "/BadValues.asset",
                    typeof(ProbeFieldsAsset).FullName!, "42"));

            AssertFailedWith(result, ErrorCodes.OW_OP_006, "values");
        }

        // The §F5 evidence row: path, nothing-there, type, declared values
        // and the expected serialized content — plus the session id.
        [Test]
        public void Object_Preflight_Snapshot_CarriesTheEvidenceRow()
        {
            var preflight = new CreateScriptableObjectOperation().Preflight(
                ObjectContext(ProbeFolder + "/Snap.asset",
                    typeof(ProbeFieldsAsset).FullName!,
                    "{ \"marker\": 5 }"));

            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((string?)snapshot["path"], Is.EqualTo(ProbeFolder + "/Snap.asset"));
            Assert.That((bool?)snapshot["pathExisted"], Is.False);
            Assert.That((string?)snapshot["type"],
                Is.EqualTo(typeof(ProbeFieldsAsset).FullName));
            Assert.That((int?)((JObject)snapshot["values"]!)["marker"], Is.EqualTo(5));
            var expected = (JObject)snapshot["expectedContent"]!;
            Assert.That(expected, Is.Not.Null,
                "the expected serialized content is the §F5 evidence");
            Assert.That((string?)expected["m_Name"], Is.EqualTo("Snap"));
            Assert.That((int?)expected["marker"], Is.EqualTo(5));
            Assert.That((string?)snapshot["editorSessionId"], Is.Not.Empty);
            Assert.That(preflight.PlannedChanges, Is.Empty);
        }

        // ══ unity.asset.create_scriptable_object — §F5 Reconcile, six cases ═

        // Case 1 — interrupted before Execute.
        [Test]
        public void Object_Reconcile_NotExecuted_ReturnsNotApplied()
        {
            var snapshot = ObjectSnapshot(ProbeFolder + "/RecNew.asset");

            var verdict = new CreateScriptableObjectOperation().Reconcile(
                ObjectContext(ProbeFolder + "/RecNew.asset"), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 2 — executed but StepSucceeded not recorded: type and
        // serialized content match the expectation.
        [Test]
        public void Object_Reconcile_ExecutedUnrecorded_ReturnsCompletedWithOutputs()
        {
            var path = ProbeFolder + "/RecDone.asset";
            var context = ObjectContext(path, null, "{ \"marker\": 11, \"label\": \"done\" }");
            var snapshot = new CreateScriptableObjectOperation().Preflight(context).Snapshot;
            Assert.That(
                new CreateScriptableObjectOperation().Execute(
                    ObjectContext(path, null, "{ \"marker\": 11, \"label\": \"done\" }")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new CreateScriptableObjectOperation().Reconcile(context, snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                () => verdict is ReconcileResult.Unknown u ? u.Reason : null);
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            var asset = (JObject)outputs["asset"].ToToken();
            Assert.That((string?)asset["kind"], Is.EqualTo("asset"));
            Assert.That((string?)asset["path"], Is.EqualTo(path));
            Assert.That((string?)asset["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets)));
        }

        // Case 3 — partial side effect: a file sits at the path but no
        // ScriptableObject is imported for it.
        [Test]
        public void Object_Reconcile_PartialSideEffect_ReturnsUnknown()
        {
            var path = ProbeFolder + "/Partial.asset";
            var snapshot = ObjectSnapshot(path);
            File.WriteAllText(PhysicalPath(path), "%YAML partial\n");
            Assert.That(AssetDatabase.LoadAssetAtPath<ScriptableObject>(path), Is.Null,
                "the partial fixture must stay un-imported for the case to exist");

            var verdict = new CreateScriptableObjectOperation().Reconcile(
                ObjectContext(path), snapshot);

            AssertUnknown(verdict, "not a ScriptableObject");
            Assert.That(File.ReadAllText(PhysicalPath(path)), Is.EqualTo("%YAML partial\n"),
                "an Unknown verdict never deletes the artifact it could not prove");
        }

        // Case 4 — pre-existing same-name artifact: recorded occupied, and
        // a foreign artifact of another type at the empty path.
        [Test]
        public void Object_Reconcile_PreexistingArtifact_ReturnsUnknown()
        {
            var path = ProbeFolder + "/PreExisting.asset";
            var snapshot = ObjectSnapshot(path);

            var seen = (JObject)snapshot.ToToken();
            seen["pathExisted"] = true;
            var probe = ScriptableObject.CreateInstance<ProbeFieldsAsset>();
            AssetDatabase.CreateAsset(probe, path);
            var seenVerdict = new CreateScriptableObjectOperation().Reconcile(
                ObjectContext(path), JsonValue.From(seen));
            AssertUnknown(seenVerdict, "pre-existing");

            // 4b: snapshot saw an empty path, then a foreign asset of a
            // different type arrived — the type no longer matches exactly.
            AssetDatabase.DeleteAsset(path);
            var foreign = ScriptableObject.CreateInstance<ProbeAsset>();
            AssetDatabase.CreateAsset(foreign, path);
            var foreignVerdict = new CreateScriptableObjectOperation().Reconcile(
                ObjectContext(path), snapshot);
            AssertUnknown(foreignVerdict, "type");
        }

        // Case 5 — the artifact's serialized content was changed after the
        // step ran.
        [Test]
        public void Object_Reconcile_ModifiedArtifact_ReturnsUnknown_AndDeletesNothing()
        {
            var path = ProbeFolder + "/Modified.asset";
            var context = ObjectContext(path, null, "{ \"marker\": 1 }");
            var snapshot = new CreateScriptableObjectOperation().Preflight(context).Snapshot;
            Assert.That(
                new CreateScriptableObjectOperation().Execute(
                    ObjectContext(path, null, "{ \"marker\": 1 }")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var asset = AssetDatabase.LoadAssetAtPath<ProbeFieldsAsset>(path);
            asset!.marker = 999;
            AssetDatabase.SaveAssetIfDirty(asset);

            var verdict = new CreateScriptableObjectOperation().Reconcile(context, snapshot);

            AssertUnknown(verdict, "does not match");
            Assert.That(AssetDatabase.LoadAssetAtPath<ProbeFieldsAsset>(path), Is.Not.Null,
                "a modified artifact is left untouched");
            Assert.That(
                Directory.GetFiles(PhysicalPath(ProbeFolder), "*.asset"), Has.Length.EqualTo(1),
                "no duplicate artifact was created");
        }

        // F3: a user-defined serialized field named like a Unity hierarchy
        // link (m_Father) must stay in the compared content — the shared
        // normalizer excludes nothing by name alone.
        [Test]
        public void Object_Reconcile_ModifiedLinkageNamedField_ReturnsUnknown()
        {
            var path = ProbeFolder + "/ModifiedNamed.asset";
            var context = ObjectContext(path, null, "{ \"m_Father\": 1 }");
            var snapshot = new CreateScriptableObjectOperation().Preflight(context).Snapshot;
            Assert.That(
                new CreateScriptableObjectOperation().Execute(
                    ObjectContext(path, null, "{ \"m_Father\": 1 }")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var asset = AssetDatabase.LoadAssetAtPath<ProbeFieldsAsset>(path);
            asset!.m_Father = 999;
            AssetDatabase.SaveAssetIfDirty(asset);

            var verdict = new CreateScriptableObjectOperation().Reconcile(context, snapshot);

            AssertUnknown(verdict, "does not match");
        }

        // Case 6 — a simulated Editor restart: the file artifact stays
        // re-identifiable by its permanent keys.
        [Test]
        public void Object_Reconcile_SimulatedRestart_StillDecidesByEvidence()
        {
            var path = ProbeFolder + "/Restart.asset";
            var context = ObjectContext(path);
            var snapshot = new CreateScriptableObjectOperation().Preflight(context).Snapshot;
            Assert.That(
                new CreateScriptableObjectOperation().Execute(ObjectContext(path)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var restarted = (JObject)snapshot.ToToken();
            restarted["editorSessionId"] = "00000000000000000000000000c0ffee";
            var restartSnapshot = JsonValue.From(restarted);

            var verdict = new CreateScriptableObjectOperation().Reconcile(context, restartSnapshot);
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "a file artifact is re-identifiable across a session boundary");

            Assert.That(AssetDatabase.DeleteAsset(path), Is.True,
                "the fixture must delete the imported probe asset");
            AssetDatabase.Refresh();
            var absent = new CreateScriptableObjectOperation().Reconcile(context, restartSnapshot);
            Assert.That(absent, Is.TypeOf<ReconcileResult.NotApplied>(),
                "an absent artifact stays safely re-runnable after a restart");
        }

        // A snapshot without its expected serialized content can prove no
        // verdict — Unknown, never Completed (§F5 common rule 6).
        [Test]
        public void Object_Reconcile_MissingExpectedContent_ReturnsUnknown()
        {
            var path = ProbeFolder + "/NoExpectation.asset";
            var context = ObjectContext(path);
            var snapshot = (JObject)new CreateScriptableObjectOperation()
                .Preflight(context).Snapshot.ToToken();
            Assert.That(
                new CreateScriptableObjectOperation().Execute(ObjectContext(path)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            snapshot.Remove("expectedContent");

            var verdict = new CreateScriptableObjectOperation().Reconcile(
                context, JsonValue.From(snapshot));

            AssertUnknown(verdict, "expected serialized content");
        }

        // ══ [UnityTest] across a real domain reload (SPEC §T.3) ════════

        private static object? s_domainSentinel;
        private static void ArmSentinel() => s_domainSentinel = new object();
        private static bool SentinelAlive => s_domainSentinel is not null;

        // A bare RecompileScripts with no script change produces no
        // domain reload: a temporary marker script under Assets makes the
        // compile real (the PrefabFingerprintReloadTests pattern); the
        // Unity setup/teardown removes it and recompiles once more.
        private static void WriteReloadMarker()
        {
            Directory.CreateDirectory(MarkerDisk);
            File.WriteAllText(
                Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
            AssetDatabase.Refresh();
        }

        private static string SnapshotStatePath(string name) =>
            Path.Combine(ProbeStateDir, name + ".json");

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;
            public static PhaseResult Pass() => new PhaseResult { Ok = true };
            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        private static PhaseResult MaterialBeforeReload()
        {
            try
            {
                var path = ProbeFolder + "/ReloadMat.mat";
                var context = MaterialContext(path, "Standard", "{ \"_Glossiness\": 0.4 }");
                var preflight = new CreateMaterialOperation().Preflight(context);
                if (preflight.Diagnostics.Count != 0)
                {
                    return PhaseResult.Fail("preflight diagnostics: " + string.Join("; ",
                        preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
                }
                var executed = new CreateMaterialOperation().Execute(context);
                if (executed.Kind != StepResultKind.Succeeded)
                {
                    return PhaseResult.Fail("execute failed: " + Describe(executed));
                }
                Directory.CreateDirectory(ProbeStateDir);
                File.WriteAllText(SnapshotStatePath("mat"), preflight.Snapshot.CanonicalText);
                WriteReloadMarker();
                ArmSentinel();
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-1 threw: " + e.Message);
            }
        }

        private static PhaseResult MaterialAfterReload()
        {
            try
            {
                var parsed = JsonValue.Parse(File.ReadAllText(SnapshotStatePath("mat")));
                if (!parsed.Ok || parsed.Value is null)
                {
                    return PhaseResult.Fail("persisted snapshot did not parse");
                }
                var path = ProbeFolder + "/ReloadMat.mat";
                var verdict = new CreateMaterialOperation().Reconcile(
                    MaterialContext(path, "Standard", "{ \"_Glossiness\": 0.4 }"),
                    parsed.Value);
                if (verdict is not ReconcileResult.Completed completed)
                {
                    return PhaseResult.Fail(
                        "expected Completed, got " +
                        (verdict is ReconcileResult.Unknown unknown
                            ? "Unknown(" + unknown.Reason + ")"
                            : verdict.GetType().Name));
                }
                var material = (JObject)completed.Outputs["material"].ToToken();
                if ((string?)material["path"] != path ||
                    (string?)material["guid"] != AssetDatabase.AssetPathToGUID(
                        path, AssetPathToGUIDOptions.OnlyExistingAssets))
                {
                    return PhaseResult.Fail("restored outputs do not match the uninterrupted run");
                }
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-2 threw: " + e.Message);
            }
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Material_Reconcile_AfterRealDomainReload_ReturnsCompleted()
        {
            var before = MaterialBeforeReload();
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            yield return new RecompileScripts();
            var guard = 0;
            while (SentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(SentinelAlive,
                "the recompile did not produce a domain reload");

            var after = MaterialAfterReload();
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        private static PhaseResult ObjectBeforeReload()
        {
            try
            {
                var path = ProbeFolder + "/ReloadObj.asset";
                var context = ObjectContext(path, null, "{ \"marker\": 77 }");
                var preflight = new CreateScriptableObjectOperation().Preflight(context);
                if (preflight.Diagnostics.Count != 0)
                {
                    return PhaseResult.Fail("preflight diagnostics: " + string.Join("; ",
                        preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
                }
                var executed = new CreateScriptableObjectOperation().Execute(context);
                if (executed.Kind != StepResultKind.Succeeded)
                {
                    return PhaseResult.Fail("execute failed: " + Describe(executed));
                }
                Directory.CreateDirectory(ProbeStateDir);
                File.WriteAllText(SnapshotStatePath("obj"), preflight.Snapshot.CanonicalText);
                WriteReloadMarker();
                ArmSentinel();
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-1 threw: " + e.Message);
            }
        }

        private static PhaseResult ObjectAfterReload()
        {
            try
            {
                var parsed = JsonValue.Parse(File.ReadAllText(SnapshotStatePath("obj")));
                if (!parsed.Ok || parsed.Value is null)
                {
                    return PhaseResult.Fail("persisted snapshot did not parse");
                }
                var path = ProbeFolder + "/ReloadObj.asset";
                var verdict = new CreateScriptableObjectOperation().Reconcile(
                    ObjectContext(path, null, "{ \"marker\": 77 }"),
                    parsed.Value);
                if (verdict is not ReconcileResult.Completed completed)
                {
                    return PhaseResult.Fail(
                        "expected Completed, got " +
                        (verdict is ReconcileResult.Unknown unknown
                            ? "Unknown(" + unknown.Reason + ")"
                            : verdict.GetType().Name));
                }
                var asset = (JObject)completed.Outputs["asset"].ToToken();
                if ((string?)asset["path"] != path ||
                    (string?)asset["guid"] != AssetDatabase.AssetPathToGUID(
                        path, AssetPathToGUIDOptions.OnlyExistingAssets))
                {
                    return PhaseResult.Fail("restored outputs do not match the uninterrupted run");
                }
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-2 threw: " + e.Message);
            }
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Object_Reconcile_AfterRealDomainReload_ReturnsCompleted()
        {
            var before = ObjectBeforeReload();
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            yield return new RecompileScripts();
            var guard = 0;
            while (SentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(SentinelAlive,
                "the recompile did not produce a domain reload");

            var after = ObjectAfterReload();
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // ══ fixtures ════════════════════════════════════════════════════

        private static UnityStepContext MaterialContext(
            string path,
            string shader,
            string? properties = null,
            IChangeRecorder? changes = null,
            IReadOnlyDictionary<string, string>? pending = null,
            bool present = true)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["path"] = present ? InputValue.Present(Json("\"" + path + "\"")) : InputValue.Missing,
                ["shader"] = InputValue.Present(Json("\"" + shader + "\"")),
            };
            if (properties is not null)
            {
                inputs["properties"] = InputValue.Present(Json(properties));
            }
            return Context(inputs, MaterialInputs, MaterialOutputs, changes, pending);
        }

        private static UnityStepContext ObjectContext(
            string path,
            string? type = null,
            string? values = null,
            IChangeRecorder? changes = null)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["path"] = InputValue.Present(Json("\"" + path + "\"")),
                ["type"] = InputValue.Present(
                    Json("\"" + (type ?? typeof(ProbeFieldsAsset).FullName!) + "\"")),
            };
            if (values is not null)
            {
                inputs["values"] = InputValue.Present(Json(values));
            }
            return Context(inputs, ObjectInputs, ObjectOutputs, changes, null);
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string> inputTypes,
            IReadOnlyDictionary<string, string> outputTypes,
            IChangeRecorder? changes,
            IReadOnlyDictionary<string, string>? pending) =>
            new UnityStepContext(
                inputs,
                inputTypes,
                outputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                changes ?? new StepChangeRecorder(0, "asset"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                null,
                pending);

        private static JsonValue MaterialSnapshot(string path, string shader)
        {
            var preflight = new CreateMaterialOperation().Preflight(
                MaterialContext(path, shader));
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => "preflight must produce the snapshot: " + string.Join("; ",
                    preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return preflight.Snapshot;
        }

        private static JsonValue ObjectSnapshot(string path)
        {
            var preflight = new CreateScriptableObjectOperation().Preflight(
                ObjectContext(path));
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => "preflight must produce the snapshot: " + string.Join("; ",
                    preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return preflight.Snapshot;
        }

        private static void EnsureFolder(string folderPath)
        {
            var segments = folderPath.Split('/');
            var parent = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var child = parent + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(child))
                {
                    Assert.That(AssetDatabase.CreateFolder(parent, segments[i]),
                        Is.Not.Empty, "the probe folder must be created: " + child);
                }
                parent = child;
            }
        }

        // A real Material asset at the path, created outside the step —
        // the fixture for the pre-existing and foreign-artifact cases.
        private static void CreateProbeMaterial(string path, string shader)
        {
            var material = new Material(Shader.Find(shader));
            AssetDatabase.CreateAsset(material, path);
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(path), Is.Not.Null,
                "the fixture material must exist: " + path);
        }

        // A real Texture2D asset imported from PNG bytes — the fixture for
        // the texture AssetRef property and object-reference values.
        private static string CreateProbeTexture()
        {
            var path = ProbeFolder + "/ProbeTex.png";
            var texture = new Texture2D(2, 2);
            File.WriteAllBytes(PhysicalPath(path), texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.LoadAssetAtPath<Texture2D>(path), Is.Not.Null,
                "the probe texture must be imported: " + path);
            return path;
        }

        // A real Shader asset declaring the four §G7 item 4 property
        // kinds the positive test applies — float/Range, Color, Vector
        // and Texture.
        private static string CreateProbeShader()
        {
            var path = ProbeFolder + "/Probe.shader";
            File.WriteAllText(PhysicalPath(path), ProbeShaderSource);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(Shader.Find(ProbeShaderName), Is.Not.Null,
                "the probe shader must resolve by name: " + ProbeShaderName);
            return ProbeShaderName;
        }

        private static void AssertFailedWith(StepResult result, string code, string? port)
        {
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed), () => Describe(result));
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo(code), () => Describe(result));
            if (port is not null)
            {
                Assert.That(result.Error!.Port, Is.EqualTo(port));
            }
        }

        private static void AssertUnknown(ReconcileResult verdict, string fragment)
        {
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => verdict is ReconcileResult.Unknown u ? u.Reason : verdict.GetType().Name);
            Assert.That(((ReconcileResult.Unknown)verdict).Reason,
                Does.Contain(fragment));
        }

        private static string Describe(StepResult result) =>
            result.Kind +
            (result.Error is null
                ? string.Empty
                : " " + result.Error.Code + " " + result.Error.Message);

        private static void AssertOutputsMatchDeclaredPortTypes(
            UnityStepContext context, IReadOnlyDictionary<string, string> declared)
        {
            foreach (var pair in declared)
            {
                var parsed = TypeRef.Parse(pair.Value);
                Assert.That(parsed.Ok, Is.True, pair.Value);
                var diagnostics = ValueValidation.Validate(
                    RunnerHostFixture.Types, parsed.Value!, context.Outputs[pair.Key], null, false);
                Assert.That(diagnostics, Is.Empty,
                    () => pair.Key + ": " + string.Join("; ",
                        diagnostics.Select(d => d.Code + " " + d.Message + " " + d.JsonPath)));
            }
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }
    }
}
