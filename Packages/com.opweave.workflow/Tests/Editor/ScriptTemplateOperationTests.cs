#nullable enable

// T-W4.2-01 [Edit] tests: local:T-W4.2-01:template-script-op and
// local:T-W4.2-01:template-script-reconcile for
// unity.script.create_from_template (SPEC §G8 item 1, §I.4, §F5).
//
// Coverage:
//  - Each bundled template renders a file whose content hash equals the
//    template output for the given className/namespace; invalid
//    identifiers are rejected (OW-OP-006); existing targets are never
//    overwritten (OW-UNITY-005); the static scan finds no
//    [InitializeOnLoad], static constructor, Editor callback or
//    [OpWeaveOperation] in any template output.
//  - The six §F5 Operation-level cases at Reconcile: not executed;
//    executed but StepSucceeded not recorded; partial side effect;
//    pre-existing same-name artifact; artifact modified by another
//    operation; simulated Editor restart (different editorSessionId,
//    discarded handles — the file artifact stays re-identifiable by its
//    permanent path/guid/content-hash keys).
//  - One [UnityTest] crosses a real domain reload (SPEC §T.3:
//    RecompileScripts + domain sentinel): the generated script's own
//    import recompiles, the iterator is resurrected with no carried
//    state, and Reconcile on the persisted Preflight snapshot returns
//    Completed with the uninterrupted outputs.

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
    /// Behaviour and §F5 reconciliation of
    /// <c>unity.script.create_from_template</c>.
    /// </summary>
    internal sealed class ScriptTemplateOperationTests
    {
        private const string ProbeFolder = "Assets/OpWeaveScriptTemplateProbe";

        private static readonly IReadOnlyDictionary<string, string> InputTypes =
            new Dictionary<string, string>
            {
                ["template"] = "core.String",
                ["className"] = "core.String",
                ["namespace"] = "core.String",
                ["folder"] = "unity.AssetPath",
            };

        private static readonly IReadOnlyDictionary<string, string> OutputTypes =
            new Dictionary<string, string>
            {
                ["script"] = "unity.AssetRef<unity.MonoScript>",
                ["typeName"] = "unity.TypeName",
            };

        // The §G8 item 1 forbidden constructs — an Editor-execution vector
        // or a new Operation — scanned on template source and output alike.
        private static readonly string[] ForbiddenTokens =
        {
            "InitializeOnLoad", "OpWeaveOperation", "UnityEditor",
            "MenuItem", "AssetPostprocessor", "EditorApplication",
            "EditorWindow", "CustomEditor", "OnPostprocessAllAssets",
            "OnWillSaveAssets", "OnOpenAsset", "DidReloadScripts",
            "InitializeOnEnterPlayMode",
        };

        private static readonly System.Text.RegularExpressions.Regex StaticCtor =
            new System.Text.RegularExpressions.Regex(
                @"static\s+[A-Za-z_][A-Za-z0-9_]*\s*\(",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        [SetUp]
        public void SetUp() => EnsureFolder(ProbeFolder);

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            var disk = Path.Combine(ProjectRoot(), ProbeFolder.Replace('/', Path.DirectorySeparatorChar));
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

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeStateDir =>
            Path.Combine(ProjectRoot(), "Library", "OpWeaveScriptTemplateProbe");

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));

        // ── Execute ───────────────────────────────────────────────────────

        // Each template generates a file whose content hash equals the
        // rendered output for the given className/namespace (§F5 evidence
        // row and the ticket's content-hash acceptance).
        [Test]
        public void Execute_GeneratesEachTemplate_WithExpectedContentAndHash(
            [Values("mono_behaviour", "scriptable_object", "plain_class")] string template)
        {
            var className = "Probe_" + template;
            var path = ProbeFolder + "/" + className + ".cs";
            var inputs = Inputs(template, className, "Game.Probes", ProbeFolder);
            var preflight = new CreateScriptFromTemplateOperation().Preflight(Context(inputs));

            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var snapshot = (JObject)preflight.Snapshot.ToToken();

            var result = new CreateScriptFromTemplateOperation().Execute(Context(inputs));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(File.Exists(PhysicalPath(path)), Is.True,
                "the step must write the script file");

            var expected = ScriptTemplates.Render(
                ScriptTemplates.ReadSource(template)!, className, "Game.Probes");
            Assert.That(expected, Is.Not.Null, "the bundled template must render");
            var onDisk = File.ReadAllText(PhysicalPath(path));
            Assert.That(onDisk, Is.EqualTo(expected),
                "the artifact must be the rendered template byte for byte");
            Assert.That(
                ScriptTemplates.ContentHash(onDisk!),
                Is.EqualTo((string?)snapshot["expectedContentHash"]),
                "the §F5 expected-content hash must match the artifact");
            Assert.That(onDisk, Does.Contain("namespace Game.Probes"));
            Assert.That(onDisk, Does.Contain("class " + className));

            Assert.That(preflight.PlannedChanges, Is.Empty);
        }

        [Test]
        public void Execute_WithoutNamespace_GeneratesGlobalClass_AndOutputsScriptAndTypeName()
        {
            var inputs = Inputs("plain_class", "GlobalProbe", null, ProbeFolder);
            var changes = new StepChangeRecorder(0, "script");
            var context = Context(inputs, changes);

            var result = new CreateScriptFromTemplateOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var path = ProbeFolder + "/GlobalProbe.cs";
            var text = File.ReadAllText(PhysicalPath(path));
            Assert.That(text, Does.Not.Contain("namespace"),
                "no namespace input means the global namespace");
            Assert.That(text, Does.Contain("class GlobalProbe"));

            var script = (JObject)context.Outputs["script"].ToToken();
            Assert.That((string?)script["kind"], Is.EqualTo("asset"));
            Assert.That((string?)script["path"], Is.EqualTo(path));
            Assert.That((string?)script["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets)));
            Assert.That((string?)context.Outputs["typeName"].ToToken(),
                Is.EqualTo("GlobalProbe"));
            AssertOutputsMatchDeclaredPortTypes(context);

            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Created));
            Assert.That(changes.Records[0].Target.Kind, Is.EqualTo("asset"));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(path));
            Assert.That(changes.Records[0].Undoable, Is.False);
        }

        [Test]
        public void Execute_NamespacedTypeName_IsQualified()
        {
            var context = Context(Inputs("mono_behaviour", "TypedProbe", "Game.Deep.Space", ProbeFolder));

            var result = new CreateScriptFromTemplateOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That((string?)context.Outputs["typeName"].ToToken(),
                Is.EqualTo("Game.Deep.Space.TypedProbe"));
            AssertOutputsMatchDeclaredPortTypes(context);
        }

        // Invalid identifiers are rejected: bad shapes and C# keywords alike,
        // on className and on every namespace segment — and an unknown
        // template id. Nothing is written for any refusal.
        [TestCase("1Foo", TestName = "ClassName_LeadingDigit_Rejected")]
        [TestCase("Foo-Bar", TestName = "ClassName_Hyphen_Rejected")]
        [TestCase("Foo.Bar", TestName = "ClassName_Dot_Rejected")]
        [TestCase("with space", TestName = "ClassName_Space_Rejected")]
        [TestCase("@foo", TestName = "ClassName_AtSign_Rejected")]
        [TestCase("int", TestName = "ClassName_Keyword_Rejected")]
        [TestCase("class", TestName = "ClassName_ClassKeyword_Rejected")]
        [TestCase("_Foo", TestName = "ClassName_UnderscoreStart_Rejected")]
        public void InvalidClassName_IsRejected_OwOp006(string className)
        {
            var path = ProbeFolder + "/" + className + ".cs";
            var result = new CreateScriptFromTemplateOperation().Execute(
                Context(Inputs("mono_behaviour", className, null, ProbeFolder)));

            AssertFailedWith(result, ErrorCodes.OW_OP_006, "className");
            Assert.That(File.Exists(PhysicalPath(path)), Is.False,
                "a refused class name must not leave a file behind");
        }

        [TestCase("Foo..Bar", TestName = "Namespace_EmptySegment_Rejected")]
        [TestCase("Foo.1Bar", TestName = "Namespace_LeadingDigitSegment_Rejected")]
        [TestCase("Foo.class", TestName = "Namespace_KeywordSegment_Rejected")]
        [TestCase(".Foo", TestName = "Namespace_LeadingDot_Rejected")]
        public void InvalidNamespace_IsRejected_OwOp006(string ns)
        {
            var result = new CreateScriptFromTemplateOperation().Execute(
                Context(Inputs("mono_behaviour", "NsProbe", ns, ProbeFolder)));

            AssertFailedWith(result, ErrorCodes.OW_OP_006, "namespace");
        }

        [Test]
        public void UnknownTemplate_IsRejected_OwOp006()
        {
            var result = new CreateScriptFromTemplateOperation().Execute(
                Context(Inputs("custom_template", "TplProbe", null, ProbeFolder)));

            AssertFailedWith(result, ErrorCodes.OW_OP_006, "template");
        }

        // A class name that repeats the selected template's base type —
        // MonoBehaviour for mono_behaviour, ScriptableObject for
        // scriptable_object — is OW-OP-006 too: the generated class
        // could not inherit itself. plain_class has no base type.
        [TestCase("mono_behaviour", "MonoBehaviour",
            TestName = "ClassName_MonoBehaviourBase_Rejected")]
        [TestCase("scriptable_object", "ScriptableObject",
            TestName = "ClassName_ScriptableObjectBase_Rejected")]
        public void ClassNameEqualToTemplateBaseType_IsRejected_OwOp006(
            string template, string className)
        {
            var result = new CreateScriptFromTemplateOperation().Execute(
                Context(Inputs(template, className, null, ProbeFolder)));

            AssertFailedWith(result, ErrorCodes.OW_OP_006, "className");
            Assert.That(File.Exists(PhysicalPath(ProbeFolder + "/" + className + ".cs")),
                Is.False, "a refused class name must not leave a file behind");
        }

        // The static check of the same rule (SPEC §C2 ValidateStatic): a
        // constant bad identifier is OW-OP-006 at validation too.
        [Test]
        public void ValidateStatic_RejectsKeywordAndBadShapeConstants()
        {
            var op = new CreateScriptFromTemplateOperation();

            var keyword = op.ValidateStatic(Static(new Dictionary<string, InputValue>
            {
                ["className"] = InputValue.Present(Json("\"int\"")),
            }));
            Assert.That(keyword.Select(d => d.Code), Contains.Item(ErrorCodes.OW_OP_006));

            var shape = op.ValidateStatic(Static(new Dictionary<string, InputValue>
            {
                ["className"] = InputValue.Present(Json("\"9x\"")),
            }));
            Assert.That(shape.Select(d => d.Code), Contains.Item(ErrorCodes.OW_OP_006));

            var ns = op.ValidateStatic(Static(new Dictionary<string, InputValue>
            {
                ["namespace"] = InputValue.Present(Json("\"Game.class\"")),
            }));
            Assert.That(ns.Select(d => d.Code), Contains.Item(ErrorCodes.OW_OP_006));

            var good = op.ValidateStatic(Static(new Dictionary<string, InputValue>
            {
                ["className"] = InputValue.Present(Json("\"Probe\"")),
                ["namespace"] = InputValue.Present(Json("\"Game.Probes\"")),
            }));
            Assert.That(good, Is.Empty);
        }

        // The constant cross-port half of the base-type rule: a constant
        // className that repeats the constant template's base type is
        // OW-OP-006 at validation; plain_class has nothing to collide with.
        [Test]
        public void ValidateStatic_RejectsConstantBaseTypeCollision()
        {
            var op = new CreateScriptFromTemplateOperation();

            var collision = op.ValidateStatic(Static(new Dictionary<string, InputValue>
            {
                ["template"] = InputValue.Present(Json("\"mono_behaviour\"")),
                ["className"] = InputValue.Present(Json("\"MonoBehaviour\"")),
            }));
            Assert.That(collision.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_OP_006));
            Assert.That(collision[0].Port, Is.EqualTo("className"));

            var plain = op.ValidateStatic(Static(new Dictionary<string, InputValue>
            {
                ["template"] = InputValue.Present(Json("\"plain_class\"")),
                ["className"] = InputValue.Present(Json("\"MonoBehaviour\"")),
            }));
            Assert.That(plain, Is.Empty,
                "plain_class has no base type to collide with");
        }

        [Test]
        public void Execute_ExistingTarget_Fails_OwUnity005_AndLeavesItAlone()
        {
            var path = ProbeFolder + "/TakenProbe.cs";
            File.WriteAllText(PhysicalPath(path), "// pre-existing\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var result = new CreateScriptFromTemplateOperation().Execute(
                Context(Inputs("mono_behaviour", "TakenProbe", null, ProbeFolder)));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "folder");
            Assert.That(File.ReadAllText(PhysicalPath(path)),
                Is.EqualTo("// pre-existing\n"),
                "the existing artifact is never overwritten");

            var preflight = new CreateScriptFromTemplateOperation().Preflight(
                Context(Inputs("mono_behaviour", "TakenProbe", null, ProbeFolder)));
            Assert.That(
                preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_005),
                "Preflight reports the occupied target too (Dry Run truth)");
        }

        [Test]
        public void Execute_MissingFolder_Fails_OwUnity005()
        {
            var result = new CreateScriptFromTemplateOperation().Execute(
                Context(Inputs("mono_behaviour", "OrphanProbe", null,
                    "Assets/OpWeaveScriptTemplateProbeNoSuchDir")));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "folder");
        }

        [Test]
        public void Execute_EscapedFolder_Fails_OwPerm002()
        {
            var result = new CreateScriptFromTemplateOperation().Execute(
                Context(Inputs("mono_behaviour", "EscapeProbe", null,
                    "Assets/../OpWeaveScriptTemplateEscape")));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "folder");
        }

        // An unreadable bundled template — a broken package — is the §J
        // import-side failure OW-UNITY-008, driven through the test seam.
        [Test]
        public void Execute_UnreadableTemplate_Fails_OwUnity008()
        {
            var result = new CreateScriptFromTemplateOperation(_ => null).Execute(
                Context(Inputs("mono_behaviour", "NoTplProbe", null, ProbeFolder)));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_008, null);
        }

        // ── §G8 item 1 static template check ─────────────────────────────

        // The SPEC §G8 template static check: no bundled template and no
        // rendered output (with and without a namespace) contains an
        // [InitializeOnLoad] / static-constructor / Editor-callback /
        // [OpWeaveOperation] vector — generated code does nothing until a
        // human uses it.
        [Test]
        public void EveryTemplate_ContainsNoEditorExecutionVector(
            [Values("mono_behaviour", "scriptable_object", "plain_class")] string template)
        {
            var source = ScriptTemplates.ReadSource(template);
            Assert.That(source, Is.Not.Null,
                "the bundled template must be readable: " + template);

            var renderedGlobal = ScriptTemplates.Render(source!, "ScanProbe", null);
            var renderedNs = ScriptTemplates.Render(source!, "ScanProbe", "Game.Scan");
            Assert.That(renderedGlobal, Is.Not.Null);
            Assert.That(renderedNs, Is.Not.Null);

            foreach (var text in new[] { source!, renderedGlobal!, renderedNs! })
            {
                foreach (var token in ForbiddenTokens)
                {
                    Assert.That(text, Does.Not.Contain(token),
                        template + " must not contain '" + token + "'");
                }
                Assert.That(StaticCtor.IsMatch(text), Is.False,
                    template + " must not contain a static constructor");
            }
        }

        // ── §F5 snapshot evidence ────────────────────────────────────────

        // The §F5 evidence row: target path, nothing-there, template id and
        // version, the inputs and the expected content hash — plus the
        // session id. A missing namespace is absent, never null (§A0).
        [Test]
        public void Preflight_Snapshot_CarriesTheEvidenceRow()
        {
            var namespaced = new CreateScriptFromTemplateOperation().Preflight(
                Context(Inputs("scriptable_object", "SnapProbe", "Game.Snap", ProbeFolder)));

            Assert.That(namespaced.Diagnostics, Is.Empty,
                () => string.Join("; ", namespaced.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var snapshot = (JObject)namespaced.Snapshot.ToToken();
            Assert.That((string?)snapshot["path"],
                Is.EqualTo(ProbeFolder + "/SnapProbe.cs"));
            Assert.That((bool?)snapshot["pathExisted"], Is.False);
            Assert.That((string?)snapshot["template"], Is.EqualTo("scriptable_object"));
            Assert.That((int?)snapshot["templateVersion"],
                Is.EqualTo(ScriptTemplates.CurrentVersion));
            Assert.That((string?)snapshot["className"], Is.EqualTo("SnapProbe"));
            Assert.That((string?)snapshot["namespace"], Is.EqualTo("Game.Snap"));
            Assert.That((string?)snapshot["expectedContentHash"], Does.Match("^[0-9a-f]{64}$"));
            Assert.That((string?)snapshot["editorSessionId"], Is.Not.Empty);

            var global = new CreateScriptFromTemplateOperation().Preflight(
                Context(Inputs("plain_class", "SnapGlobal", null, ProbeFolder)));
            Assert.That(global.Diagnostics, Is.Empty);
            Assert.That(((JObject)global.Snapshot.ToToken())["namespace"], Is.Null,
                "a missing namespace is omitted, never serialized as null");
        }

        // ── §F5 Reconcile — the six cases ────────────────────────────────

        // Case 1 — interrupted before Execute: nothing at the target is
        // provably not applied, and re-execution is safe.
        [Test]
        public void Reconcile_NotExecuted_ReturnsNotApplied()
        {
            var snapshot = PreflightSnapshot("plain_class", "ReconcileNew", null);

            var verdict = new CreateScriptFromTemplateOperation().Reconcile(
                Context(Inputs("plain_class", "ReconcileNew", null, ProbeFolder)), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 2 — interrupted after Execute before StepSucceeded: the file
        // matches the expected hash and imports as a MonoScript, so the
        // verdict completes with the uninterrupted run's outputs.
        [Test]
        public void Reconcile_ExecutedUnrecorded_ReturnsCompletedWithOutputs()
        {
            var inputs = Inputs("mono_behaviour", "ReconcileDone", "Game.R", ProbeFolder);
            var snapshot = new CreateScriptFromTemplateOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateScriptFromTemplateOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded),
                "the uninterrupted run must have produced the artifact");

            var verdict = new CreateScriptFromTemplateOperation().Reconcile(
                Context(inputs), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            var path = ProbeFolder + "/ReconcileDone.cs";
            var script = (JObject)outputs["script"].ToToken();
            Assert.That((string?)script["kind"], Is.EqualTo("asset"));
            Assert.That((string?)script["path"], Is.EqualTo(path));
            Assert.That((string?)script["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets)));
            Assert.That((string?)outputs["typeName"].ToToken(),
                Is.EqualTo("Game.R.ReconcileDone"));
        }

        // Case 3 — partial side effect: the expected bytes sit on disk but
        // the database never imported them as a script; that is not a
        // Completed the evidence allows, and the file being there rules
        // out NotApplied — Unknown, NeedsReview, never re-run.
        [Test]
        public void Reconcile_PartialSideEffect_ReturnsUnknown()
        {
            var className = "PartialProbe";
            var path = ProbeFolder + "/" + className + ".cs";
            var snapshot = PreflightSnapshot("mono_behaviour", className, null);
            var rendered = ScriptTemplates.Render(
                ScriptTemplates.ReadSource("mono_behaviour")!, className, null)!;
            File.WriteAllText(PhysicalPath(path), rendered, ScriptTemplates.Encoding);
            Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(path), Is.Null,
                "the partial fixture must stay un-imported for the case to exist");
            Assert.That(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets), Is.Empty);

            var verdict = new CreateScriptFromTemplateOperation().Reconcile(
                Context(Inputs("mono_behaviour", className, null, ProbeFolder)), snapshot);

            AssertUnknown(verdict, "not imported");
            Assert.That(File.ReadAllText(PhysicalPath(path)), Is.EqualTo(rendered),
                "an Unknown verdict never deletes the artifact it could not prove");
        }

        // Case 4 — a pre-existing same-name artifact, both recorded
        // variants: the snapshot that itself saw the file, and a file of
        // foreign content that arrived after the empty-path snapshot.
        // Neither can be Completed.
        [Test]
        public void Reconcile_PreexistingArtifact_ReturnsUnknown()
        {
            var className = "PreExistingProbe";
            var path = ProbeFolder + "/" + className + ".cs";
            var snapshot = PreflightSnapshot("plain_class", className, null);

            // 4a: the snapshot recorded the file as pre-existing.
            var seen = (JObject)snapshot.ToToken();
            seen["pathExisted"] = true;
            File.WriteAllText(PhysicalPath(path), "// someone else's file\n");
            var seenVerdict = new CreateScriptFromTemplateOperation().Reconcile(
                Context(Inputs("plain_class", className, null, ProbeFolder)),
                JsonValue.From(seen));
            AssertUnknown(seenVerdict, "pre-existing");
            Assert.That(File.ReadAllText(PhysicalPath(path)),
                Is.EqualTo("// someone else's file\n"),
                "the pre-existing artifact is never deleted or claimed");

            // 4b: snapshot saw an empty path, then foreign content arrived.
            File.WriteAllText(PhysicalPath(path), "// changed again\n");
            var foreignVerdict = new CreateScriptFromTemplateOperation().Reconcile(
                Context(Inputs("plain_class", className, null, ProbeFolder)),
                snapshot);
            Assert.That(foreignVerdict, Is.TypeOf<ReconcileResult.Unknown>(),
                "content the template did not produce is not the step's artifact");
            AssertUnknown(foreignVerdict, "does not match");
        }

        // Case 5 — the artifact was changed by another operation after the
        // step ran: content no longer matches the expectation.
        [Test]
        public void Reconcile_ModifiedArtifact_ReturnsUnknown_AndDeletesNothing()
        {
            var className = "ModifiedProbe";
            var inputs = Inputs("plain_class", className, null, ProbeFolder);
            var snapshot = new CreateScriptFromTemplateOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateScriptFromTemplateOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            var path = ProbeFolder + "/" + className + ".cs";
            File.WriteAllText(PhysicalPath(path), "// modified by another operation\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var verdict = new CreateScriptFromTemplateOperation().Reconcile(
                Context(inputs), snapshot);

            AssertUnknown(verdict, "does not match");
            Assert.That(File.ReadAllText(PhysicalPath(path)),
                Is.EqualTo("// modified by another operation\n"),
                "a modified artifact is left untouched");
            Assert.That(Directory.GetFiles(PhysicalPath(ProbeFolder), "*.cs"), Has.Length.EqualTo(1),
                "no duplicate artifact was created");
        }

        // Case 6 — a simulated Editor restart (different editorSessionId
        // carried on the snapshot, discarded operation handles): the file
        // artifact stays re-identifiable by its permanent keys, so the
        // verdict still follows the evidence — Completed when the artifact
        // is whole, NotApplied when it is absent (§F5 common rule 3).
        [Test]
        public void Reconcile_SimulatedRestart_StillDecidesByEvidence()
        {
            var className = "RestartProbe";
            var inputs = Inputs("mono_behaviour", className, null, ProbeFolder);
            var snapshot = new CreateScriptFromTemplateOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateScriptFromTemplateOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var restarted = (JObject)snapshot.ToToken();
            restarted["editorSessionId"] = "00000000000000000000000000c0ffee";
            var restartSnapshot = JsonValue.From(restarted);

            var verdict = new CreateScriptFromTemplateOperation().Reconcile(
                Context(inputs), restartSnapshot);
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "a file artifact is re-identifiable across a session boundary");

            var path = ProbeFolder + "/" + className + ".cs";
            Assert.That(AssetDatabase.DeleteAsset(path), Is.True,
                "the fixture must delete the imported probe asset");
            AssetDatabase.Refresh();
            Assert.That(File.Exists(PhysicalPath(path)), Is.False,
                "a truly absent artifact leaves no file on disk");
            Assert.That(File.Exists(PhysicalPath(path) + ".meta"), Is.False,
                "a truly absent artifact leaves no orphan .meta");
            Assert.That(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets), Is.Empty,
                "a truly absent artifact leaves no existing AssetDatabase asset");
            var absent = new CreateScriptFromTemplateOperation().Reconcile(
                Context(inputs), restartSnapshot);
            Assert.That(absent, Is.TypeOf<ReconcileResult.NotApplied>(),
                "an absent artifact stays safely re-runnable after a restart");
        }

        // The leftover-occupant cases: NotApplied needs a truly empty
        // target — no file, no .meta and no existing asset (§F5). A
        // Refresh after deleting the file cannot stage that state
        // because Unity removes the orphan .meta and the database entry
        // itself, so the fixture deletes the file on disk without a
        // refresh: the .meta — and the database's memory of the asset —
        // stay behind. A bare orphan .meta the database never imported
        // occupies the path the same way. Both are deliberately Unknown,
        // never NotApplied: re-execution would refuse an occupied target.
        [Test]
        public void Reconcile_OrphanMetaWithoutFile_ReturnsUnknown()
        {
            var className = "OrphanMetaProbe";
            var path = ProbeFolder + "/" + className + ".cs";
            var inputs = Inputs("mono_behaviour", className, null, ProbeFolder);
            var snapshot = new CreateScriptFromTemplateOperation().Preflight(Context(inputs)).Snapshot;
            Assert.That(
                new CreateScriptFromTemplateOperation().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            File.Delete(PhysicalPath(path));
            Assert.That(File.Exists(PhysicalPath(path)), Is.False,
                "the fixture must remove the asset file only");
            Assert.That(File.Exists(PhysicalPath(path) + ".meta"), Is.True,
                "without a refresh the .meta outlives the file");

            var verdict = new CreateScriptFromTemplateOperation().Reconcile(
                Context(inputs), snapshot);

            AssertUnknown(verdict, "without a file");

            var orphanClass = "BareOrphanProbe";
            var orphanPath = ProbeFolder + "/" + orphanClass + ".cs";
            var orphanInputs = Inputs("mono_behaviour", orphanClass, null, ProbeFolder);
            var orphanSnapshot = new CreateScriptFromTemplateOperation()
                .Preflight(Context(orphanInputs)).Snapshot;
            File.WriteAllText(PhysicalPath(orphanPath) + ".meta",
                "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n");
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    orphanPath, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.Empty,
                "the never-imported path must have no existing asset");

            var orphanVerdict = new CreateScriptFromTemplateOperation().Reconcile(
                Context(orphanInputs), orphanSnapshot);

            AssertUnknown(orphanVerdict, "leftover .meta");
        }

        // Evidence that cannot be re-established is Unknown — never
        // Completed, never NotApplied.
        [Test]
        public void Reconcile_MalformedOrUnestablishedEvidence_ReturnsUnknown()
        {
            var snapshot = PreflightSnapshot("plain_class", "EvidenceProbe", null);

            Assert.That(
                new CreateScriptFromTemplateOperation().Reconcile(
                    Context(Inputs("plain_class", "EvidenceProbe", null, ProbeFolder)),
                    JsonValue.From(new JValue(5))),
                Is.TypeOf<ReconcileResult.Unknown>(),
                "a non-object snapshot restores no evidence");

            var missing = (JObject)snapshot.ToToken();
            missing.Remove("expectedContentHash");
            AssertUnknown(
                new CreateScriptFromTemplateOperation().Reconcile(
                    Context(Inputs("plain_class", "EvidenceProbe", null, ProbeFolder)),
                    JsonValue.From(missing)),
                "missing");

            var badTemplate = (JObject)snapshot.ToToken();
            badTemplate["template"] = "bogus";
            AssertUnknown(
                new CreateScriptFromTemplateOperation().Reconcile(
                    Context(Inputs("plain_class", "EvidenceProbe", null, ProbeFolder)),
                    JsonValue.From(badTemplate)),
                "template");

            var badVersion = (JObject)snapshot.ToToken();
            badVersion["templateVersion"] = 99;
            AssertUnknown(
                new CreateScriptFromTemplateOperation().Reconcile(
                    Context(Inputs("plain_class", "EvidenceProbe", null, ProbeFolder)),
                    JsonValue.From(badVersion)),
                "template");

            var badHash = (JObject)snapshot.ToToken();
            badHash["expectedContentHash"] = "not-hex";
            AssertUnknown(
                new CreateScriptFromTemplateOperation().Reconcile(
                    Context(Inputs("plain_class", "EvidenceProbe", null, ProbeFolder)),
                    JsonValue.From(badHash)),
                "hash");
        }

        // ── [UnityTest] across a real domain reload (SPEC §T.3) ──────────

        private static object? s_domainSentinel;
        private static void ArmSentinel() => s_domainSentinel = new object();
        private static bool SentinelAlive => s_domainSentinel is not null;

        private static string SnapshotStatePath =>
            Path.Combine(ProbeStateDir, "snapshot.txt");

        private static string SessionProbePath =>
            Path.Combine(ProbeStateDir, "session.txt");

        // T-W2.4-09: a per-Editor-session token phase-1 stores in
        // SessionState and writes into the session probe file beside the
        // persisted snapshot (a single JSON document that cannot carry the
        // token); phase-2 reads both to prove the persisted state belongs
        // to this session's phase-1 rather than residue of an earlier run
        // (the ChangeSetDomainReloadTests pattern).
        private const string ProbeTokenKey = "OpWeave.W2409.ScriptTemplate.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume
        // is detected. leg is a method local, so a real reload resets it;
        // the this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[W2409-template] " + message);

        // The phase-1 probe proves phase-1 ran in THIS Editor session: the
        // snapshot file must exist AND the session file's content must
        // equal the token phase-1 stored in SessionState. Both Library/ and
        // SessionState survive the reload, so a stale-resume hijack
        // (phase-1 skipped: no file, no token) and residue of an earlier
        // session (file yes, token no) both fail the check — the caller
        // then re-drives a real phase-1 instead of skipping into phase-2.
        private static bool ProbeWrittenThisSession()
        {
            if (!File.Exists(SnapshotStatePath) || !File.Exists(SessionProbePath))
            {
                return false;
            }
            var expectedToken = SessionState.GetString(ProbeTokenKey, "");
            return expectedToken.Length > 0 &&
                File.ReadAllText(SessionProbePath).Trim() == expectedToken;
        }

        // A stale-resume re-drive repeats Execute on the same target path,
        // so the generated script a hijacked or abandoned earlier leg left
        // behind must go first — its byte-identical rewrite would otherwise
        // queue no compile.
        private static void CleanupForReDrive()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            var disk = Path.Combine(
                ProjectRoot(), ProbeFolder.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(disk))
            {
                try { Directory.Delete(disk, recursive: true); } catch (Exception) { }
            }
            if (File.Exists(disk + ".meta"))
            {
                try { File.Delete(disk + ".meta"); } catch (Exception) { }
            }
            AssetDatabase.Refresh();
            EnsureFolder(ProbeFolder);
        }

        [Serializable]
        private sealed class PhaseResult
        {
            public bool Ok;
            public string Failure = string.Empty;
            public static PhaseResult Pass() => new PhaseResult { Ok = true };
            public static PhaseResult Fail(string message) =>
                new PhaseResult { Failure = message };
        }

        // Executes the step, persists the Preflight snapshot and the target
        // path under Library/ (UTF locals do not survive the reload), then
        // lets the generated script's import trigger the recompile.
        private static PhaseResult PhaseBeforeReload()
        {
            try
            {
                var className = "ReloadProbeScript";
                var inputs = Inputs("mono_behaviour", className, "Game.Reload", ProbeFolder);
                var preflight = new CreateScriptFromTemplateOperation().Preflight(Context(inputs));
                if (preflight.Diagnostics.Count != 0)
                {
                    return PhaseResult.Fail(
                        "preflight diagnostics: " + string.Join("; ",
                            preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
                }
                var executed = new CreateScriptFromTemplateOperation().Execute(Context(inputs));
                if (executed.Kind != StepResultKind.Succeeded)
                {
                    return PhaseResult.Fail("execute failed: " + Describe(executed));
                }
                Directory.CreateDirectory(ProbeStateDir);
                File.WriteAllText(SnapshotStatePath, preflight.Snapshot.CanonicalText);
                var token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
                File.WriteAllText(SessionProbePath, token + "\n");
                ArmSentinel();
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-1 threw: " + e.Message);
            }
        }

        // After the reload: re-read the persisted snapshot (nothing is
        // carried in memory) and reconcile — the file the step wrote is
        // still the expected-content, imported MonoScript, so the verdict
        // is Completed with the uninterrupted outputs restored.
        private static PhaseResult PhaseAfterReload()
        {
            try
            {
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(SnapshotStatePath))
                {
                    return PhaseResult.Fail(
                        expectedToken.Length == 0
                            ? "phase-1 never executed in this Editor session (" +
                                SnapshotStatePath + " was never written; " +
                                "probeDir exists=" + Directory.Exists(ProbeStateDir) + ")"
                            : "the persisted snapshot " + SnapshotStatePath +
                                " was deleted after phase-1 wrote it");
                }
                if (expectedToken.Length == 0 || !File.Exists(SessionProbePath) ||
                    File.ReadAllText(SessionProbePath).Trim() != expectedToken)
                {
                    return PhaseResult.Fail(
                        "the persisted probe state is stale — a previous " +
                        "Editor session wrote " + SessionProbePath);
                }
                var parsed = JsonValue.Parse(File.ReadAllText(SnapshotStatePath));
                if (!parsed.Ok || parsed.Value is null)
                {
                    return PhaseResult.Fail("persisted snapshot did not parse");
                }
                var verdict = new CreateScriptFromTemplateOperation().Reconcile(
                    Context(Inputs(
                        "mono_behaviour", "ReloadProbeScript", "Game.Reload", ProbeFolder)),
                    parsed.Value);
                if (verdict is not ReconcileResult.Completed completed)
                {
                    return PhaseResult.Fail(
                        "expected Completed, got " +
                        (verdict is ReconcileResult.Unknown unknown
                            ? "Unknown(" + unknown.Reason + ")"
                            : verdict.GetType().Name));
                }
                var script = (JObject)completed.Outputs["script"].ToToken();
                var path = ProbeFolder + "/ReloadProbeScript.cs";
                if ((string?)script["path"] != path ||
                    (string?)script["guid"] != AssetDatabase.AssetPathToGUID(
                        path, AssetPathToGUIDOptions.OnlyExistingAssets) ||
                    (string?)completed.Outputs["typeName"].ToToken() !=
                        "Game.Reload.ReloadProbeScript")
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
        public IEnumerator Reconcile_AfterRealDomainReload_ReturnsCompleted()
        {
            var before = PhaseBeforeReload();
            Crumb("phase-1 returned ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            // T-W2.4-09 stale-resume self-heal: a predecessor's captured UTF
            // resume PC applies to this fresh enumerator and lands it at one
            // of the two post-yield states inside this loop — with every
            // local (leg included) reset to default. Both continuations
            // drain, then MUST pass the this-session probe check: a probe
            // phase-1 never wrote re-drives a real phase-1 + compile +
            // reload instead of skipping silently into phase-2. If the
            // probe still cannot be produced after the bound, the loop
            // exits and phase-2's existing never-executed diagnostic fails
            // loudly.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (SentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(SentinelAlive,
                    "the script import did not produce a domain reload");

                if (ProbeWrittenThisSession() || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupForReDrive();
                var redrive = PhaseBeforeReload();
                Crumb("phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = PhaseAfterReload();
            Crumb("phase-2 returned ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // ── fixtures ─────────────────────────────────────────────────────

        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;
            internal StubStaticContext(IReadOnlyDictionary<string, InputValue> parameters)
            {
                _parameters = parameters;
            }
            public override string NodeId => "script";
            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;
            public override T Get<T>(string port) =>
                throw new NotSupportedException("identifier checks read Parameters only");
        }

        private static StaticContext Static(IReadOnlyDictionary<string, InputValue> parameters) =>
            new StubStaticContext(parameters);

        private static Dictionary<string, InputValue> Inputs(
            string template, string className, string? ns, string folder)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["template"] = InputValue.Present(Json("\"" + template + "\"")),
                ["className"] = InputValue.Present(Json("\"" + className + "\"")),
                ["folder"] = InputValue.Present(Json("\"" + folder + "\"")),
            };
            if (ns is not null)
            {
                inputs["namespace"] = InputValue.Present(Json("\"" + ns + "\""));
            }
            return inputs;
        }

        private static JsonValue PreflightSnapshot(string template, string className, string? ns)
        {
            var preflight = new CreateScriptFromTemplateOperation().Preflight(
                Context(Inputs(template, className, ns, ProbeFolder)));
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => "preflight must produce the snapshot: " + string.Join("; ",
                    preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return preflight.Snapshot;
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IChangeRecorder? changes = null) =>
            new UnityStepContext(
                inputs,
                InputTypes,
                OutputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                changes ?? new StepChangeRecorder(0, "script"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));

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

        private static void AssertOutputsMatchDeclaredPortTypes(UnityStepContext context)
        {
            foreach (var pair in OutputTypes)
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
