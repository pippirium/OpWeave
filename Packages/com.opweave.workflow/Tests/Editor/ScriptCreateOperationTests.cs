#nullable enable

// T-W4.2-02 [Edit] tests: the unity.script.create halves of
// local:T-W4.2-02:script-create-compile and
// local:T-W4.2-02:script-create-reconcile (SPEC §G8 item 2, §I.4, §F5).
//
// Coverage:
//  - Arbitrary content is written byte-exact UTF-8-no-BOM under the
//    configured codePaths and imported as a MonoScript; the script output
//    carries the fully-keyed asset Ref; a Created (or Modified on
//    overwrite) change is recorded.
//  - The §G8 item 2 bounds: a non-.cs path is OW-OP-006; a path outside
//    codePaths — including the custom Operation folder, which is not a
//    default code root — is OW-PERM-002 at Preflight/Execute and at
//    ValidateStatic; an unloadable codePaths source fails closed the same
//    way; a non-overwrite write over an occupied path and a missing
//    folder are OW-UNITY-005.
//  - The six §F5 cases at Reconcile: not executed; executed but
//    StepSucceeded not recorded; partial side effect (file written, not
//    imported); pre-existing same-name artifact (both snapshot variants
//    plus the overwrite pre-state); artifact modified by another
//    operation; simulated Editor restart. Plus the leftover-occupant and
//    malformed-evidence Unknown cases.

using System;
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

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Behaviour and §F5 reconciliation of <c>unity.script.create</c>.
    /// </summary>
    internal sealed class ScriptCreateOperationTests
    {
        private const string ProbeFolder = "Assets/Scripts/OpWeaveScriptCreateProbe";
        private const string OutsideFolder = "Assets/OpWeaveScriptCreateOutside";

        private static readonly IReadOnlyList<string> CodePaths =
            new[] { "Assets/Scripts/" };

        private static readonly IReadOnlyDictionary<string, string> InputTypes =
            new Dictionary<string, string>
            {
                ["path"] = "unity.AssetPath",
                ["content"] = "core.String",
                ["overwrite"] = "core.Bool",
            };

        private static readonly IReadOnlyDictionary<string, string> OutputTypes =
            new Dictionary<string, string>
            {
                ["script"] = "unity.AssetRef<unity.MonoScript>",
            };

        private const string ProbeSource =
            "using UnityEngine;\n" +
            "\n" +
            "namespace Game.Probes\n" +
            "{\n" +
            "    public sealed class CreateProbe : MonoBehaviour\n" +
            "    {\n" +
            "    }\n" +
            "}\n";

        private const string OverwriteSource =
            "public static class OverwriteProbe { public const int V = 2; }\n";

        private bool _createdScriptsFolder;

        [SetUp]
        public void SetUp()
        {
            _createdScriptsFolder = !AssetDatabase.IsValidFolder("Assets/Scripts");
            EnsureFolder(ProbeFolder);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var folder in new[] { ProbeFolder, OutsideFolder })
            {
                if (AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                }
                var disk = Path.Combine(
                    ProjectRoot(), folder.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(disk))
                {
                    try { Directory.Delete(disk, recursive: true); } catch (Exception) { }
                }
                if (File.Exists(disk + ".meta"))
                {
                    try { File.Delete(disk + ".meta"); } catch (Exception) { }
                }
            }
            if (_createdScriptsFolder && AssetDatabase.IsValidFolder("Assets/Scripts") &&
                AssetDatabase.FindAssets(string.Empty, new[] { "Assets/Scripts" }).Length == 0)
            {
                AssetDatabase.DeleteAsset("Assets/Scripts");
            }
            AssetDatabase.Refresh();
        }

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));

        // The production Operation over the test's fixed code-path seam —
        // the live settings file's value is replaced, the §G8 bound stays.
        private static CreateScriptOperation Op(
            IReadOnlyList<string>? codePaths = null) =>
            new CreateScriptOperation(() => codePaths ?? CodePaths);

        // ── Execute ───────────────────────────────────────────────────────

        // The stage-3 write: arbitrary content lands byte-exact and imports
        // as a MonoScript — content no template produced is the point of
        // stage 3 (SPEC §G8 item 2).
        [Test]
        public void Execute_WritesArbitraryContent_ImportsMonoScript_OutputsScript()
        {
            var path = ProbeFolder + "/CreateProbe.cs";
            var inputs = Inputs(path, ProbeSource);
            var changes = new StepChangeRecorder(0, "script");
            var context = Context(inputs, changes);

            var result = Op().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(File.ReadAllText(PhysicalPath(path)), Is.EqualTo(ProbeSource),
                "the artifact is the approved content byte for byte");
            Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(path), Is.Not.Null,
                "the written file must import as a MonoScript");

            var script = (JObject)context.Outputs["script"].ToToken();
            Assert.That((string?)script["kind"], Is.EqualTo("asset"));
            Assert.That((string?)script["path"], Is.EqualTo(path));
            Assert.That((string?)script["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets)));

            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Created));
            Assert.That(changes.Records[0].Target.Kind, Is.EqualTo("asset"));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(path));
            Assert.That(changes.Records[0].Undoable, Is.False);
        }

        // A .cs suffix is the §I.4 "AssetPath(.cs)" contract — anything
        // else is OW-OP-006, at Execute and at the constant ValidateStatic.
        [Test]
        public void Execute_NonScriptPath_Fails_OwOp006()
        {
            var result = Op().Execute(
                Context(Inputs(ProbeFolder + "/note.txt", "text")));

            AssertFailedWith(result, ErrorCodes.OW_OP_006, "path");
            Assert.That(File.Exists(PhysicalPath(ProbeFolder + "/note.txt")), Is.False,
                "a refused path writes nothing");

            var diagnostics = Op().ValidateStatic(Static(new Dictionary<string, InputValue>
            {
                ["path"] = InputValue.Present(Json("\"Assets/Scripts/note.txt\"")),
            }));
            Assert.That(diagnostics.Select(d => d.Code), Contains.Item(ErrorCodes.OW_OP_006));
        }

        // The code-path bound (SPEC §G8 item 2): outside codePaths is
        // OW-PERM-002 — at Preflight and Execute through the injected
        // seam, and at ValidateStatic for a constant path.
        [Test]
        public void OutsideCodePaths_Fails_OwPerm002()
        {
            EnsureFolder(OutsideFolder);
            var path = OutsideFolder + "/Outside.cs";

            var execute = Op().Execute(Context(Inputs(path, ProbeSource)));
            AssertFailedWith(execute, ErrorCodes.OW_PERM_002, "path");

            var preflight = Op().Preflight(Context(Inputs(path, ProbeSource)));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_PERM_002));

            var diagnostics = Op().ValidateStatic(Static(new Dictionary<string, InputValue>
            {
                ["path"] = InputValue.Present(Json("\"Assets/Outside/X.cs\"")),
            }));
            Assert.That(diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_PERM_002));
            Assert.That(File.Exists(PhysicalPath(path)), Is.False,
                "a refused path writes nothing");
        }

        // The custom Operation folder is deliberately outside the default
        // codePaths — a human opts in by adding it (SPEC §G8 item 2, §M.7):
        // with the default roots, writing under it is OW-PERM-002; once the
        // settings name it, the same path is admitted.
        [Test]
        public void CustomOperationFolder_IsNotADefaultCodePath()
        {
            var denied = new CreateScriptOperation().ValidateStatic(
                Static(new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(
                        Json("\"Assets/OpWeave/Editor/Operations/Foo.cs\"")),
                }));
            Assert.That(denied.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_PERM_002),
                "the default codePaths must not cover the custom Operation folder");

            var admitted = Op(new[] { "Assets/Scripts/", "Assets/OpWeave/Editor/Operations/" })
                .ValidateStatic(Static(new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(
                        Json("\"Assets/OpWeave/Editor/Operations/Foo.cs\"")),
                }));
            Assert.That(admitted, Is.Empty,
                "an explicitly configured code path admits the write");
        }

        // A codePaths source that cannot answer — the settings file failed
        // to load — admits no write at all (fail closed, D-R6).
        [Test]
        public void Execute_CodePathsUnavailable_FailsClosed_OwPerm002()
        {
            var op = new CreateScriptOperation(() => null);

            var result = op.Execute(Context(Inputs(ProbeFolder + "/Closed.cs", ProbeSource)));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "path");
        }

        [Test]
        public void Execute_EscapedPath_Fails_OwPerm002()
        {
            var result = Op().Execute(
                Context(Inputs("Assets/Scripts/../escape.cs", ProbeSource)));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "path");
        }

        [Test]
        public void Execute_ExistingTarget_NoOverwrite_Fails_OwUnity005()
        {
            var path = ProbeFolder + "/Taken.cs";
            File.WriteAllText(PhysicalPath(path), "// pre-existing\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var result = Op().Execute(Context(Inputs(path, ProbeSource)));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
            Assert.That(File.ReadAllText(PhysicalPath(path)),
                Is.EqualTo("// pre-existing\n"),
                "an existing artifact is never overwritten without overwrite");

            var preflight = Op().Preflight(Context(Inputs(path, ProbeSource)));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_005),
                "Preflight reports the occupied target too (Dry Run truth)");
        }

        // overwrite: the file is rewritten in place — the imported asset
        // keeps its guid — and the change record is Modified; the Preflight
        // snapshot records the previous content's hash as the §F5 pre-state.
        [Test]
        public void Execute_OverwriteTrue_RewritesInPlace_RecordsModified()
        {
            var path = ProbeFolder + "/Rewritten.cs";
            File.WriteAllText(PhysicalPath(path), "// first version\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);

            var preflight = Op().Preflight(Context(Inputs(path, OverwriteSource, overwrite: true)));
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(d => d.Code)));
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((bool?)snapshot["pathExisted"], Is.True);
            Assert.That((string?)snapshot["previousContentHash"],
                Is.EqualTo(ScriptTemplates.ContentHash("// first version\n")));

            var changes = new StepChangeRecorder(0, "script");
            var context = Context(Inputs(path, OverwriteSource, overwrite: true), changes);
            var result = Op().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(File.ReadAllText(PhysicalPath(path)), Is.EqualTo(OverwriteSource));
            Assert.That(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.EqualTo(guid), "an overwrite keeps the imported asset's identity");
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Modified));
        }

        [Test]
        public void Execute_MissingFolder_Fails_OwUnity005()
        {
            var result = Op().Execute(
                Context(Inputs("Assets/Scripts/OpWeaveNoSuchFolder/X.cs", ProbeSource)));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
        }

        // ── §F5 snapshot evidence ────────────────────────────────────────

        // The §F5 evidence row: normalized path, nothing-there pre-state,
        // the input content's hash, the overwrite flag and the session id —
        // previousContentHash appears only when overwrite admits a
        // pre-existing file.
        [Test]
        public void Preflight_Snapshot_CarriesTheEvidenceRow()
        {
            var path = ProbeFolder + "/SnapProbe.cs";
            var preflight = Op().Preflight(Context(Inputs(path, ProbeSource)));

            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(d => d.Code)));
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((string?)snapshot["path"], Is.EqualTo(path));
            Assert.That((bool?)snapshot["pathExisted"], Is.False);
            Assert.That((string?)snapshot["expectedContentHash"],
                Is.EqualTo(ScriptTemplates.ContentHash(ProbeSource)));
            Assert.That((bool?)snapshot["overwrite"], Is.False);
            Assert.That((string?)snapshot["editorSessionId"], Is.Not.Empty);
            Assert.That(snapshot["previousContentHash"], Is.Null,
                "no pre-existing file means no previous-content member");
        }

        // ── §F5 Reconcile — the six cases ────────────────────────────────

        // Case 1 — interrupted before Execute: nothing at the target is
        // provably not applied, and re-execution is safe.
        [Test]
        public void Reconcile_NotExecuted_ReturnsNotApplied()
        {
            var path = ProbeFolder + "/ReconcileNew.cs";
            var snapshot = PreflightSnapshot(Inputs(path, ProbeSource));

            var verdict = Op().Reconcile(Context(Inputs(path, ProbeSource)), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 2 — interrupted after Execute before StepSucceeded: the
        // file matches the expected hash and imports as a MonoScript, so
        // the verdict completes with the uninterrupted run's outputs.
        [Test]
        public void Reconcile_ExecutedUnrecorded_ReturnsCompletedWithOutputs()
        {
            var path = ProbeFolder + "/ReconcileDone.cs";
            var inputs = Inputs(path, ProbeSource);
            var snapshot = PreflightSnapshot(inputs);
            Assert.That(Op().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded),
                "the uninterrupted run must have produced the artifact");

            var verdict = Op().Reconcile(Context(inputs), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
            var script = (JObject)((ReconcileResult.Completed)verdict)
                .Outputs["script"].ToToken();
            Assert.That((string?)script["kind"], Is.EqualTo("asset"));
            Assert.That((string?)script["path"], Is.EqualTo(path));
            Assert.That((string?)script["guid"],
                Is.EqualTo(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets)));
        }

        // Case 3 — partial side effect: the expected bytes sit on disk but
        // the database never imported them; not the Completed the evidence
        // allows, and the file rules out NotApplied — Unknown, never re-run.
        [Test]
        public void Reconcile_PartialSideEffect_ReturnsUnknown()
        {
            var path = ProbeFolder + "/PartialProbe.cs";
            var snapshot = PreflightSnapshot(Inputs(path, ProbeSource));
            File.WriteAllText(PhysicalPath(path), ProbeSource, ScriptTemplates.Encoding);
            Assert.That(AssetDatabase.LoadAssetAtPath<MonoScript>(path), Is.Null,
                "the partial fixture must stay un-imported for the case to exist");
            Assert.That(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets), Is.Empty);

            var verdict = Op().Reconcile(Context(Inputs(path, ProbeSource)), snapshot);

            AssertUnknown(verdict, "not imported");
            Assert.That(File.ReadAllText(PhysicalPath(path)), Is.EqualTo(ProbeSource),
                "an Unknown verdict never deletes the artifact it could not prove");
        }

        // Case 4 — a pre-existing same-name artifact, both recorded
        // variants: a snapshot claiming the file existed but carrying no
        // previous hash is malformed evidence, and foreign content that
        // arrived after an empty-path snapshot matches nothing.
        [Test]
        public void Reconcile_PreexistingArtifact_ReturnsUnknown()
        {
            var path = ProbeFolder + "/PreExisting.cs";
            var snapshot = PreflightSnapshot(Inputs(path, ProbeSource));

            // 4a: pathExisted without the previous hash the overwrite
            // evidence requires — unrestorable evidence is Unknown.
            var seen = (JObject)snapshot.ToToken();
            seen["pathExisted"] = true;
            File.WriteAllText(PhysicalPath(path), "// someone else's file\n");
            var seenVerdict = Op().Reconcile(
                Context(Inputs(path, ProbeSource)), JsonValue.From(seen));
            AssertUnknown(seenVerdict, "content hash");
            Assert.That(File.ReadAllText(PhysicalPath(path)),
                Is.EqualTo("// someone else's file\n"),
                "the pre-existing artifact is never deleted or claimed");

            // 4b: the snapshot saw an empty path; foreign content arrived.
            File.WriteAllText(PhysicalPath(path), "// changed again\n");
            var foreignVerdict = Op().Reconcile(
                Context(Inputs(path, ProbeSource)), snapshot);
            AssertUnknown(foreignVerdict, "matches neither");
        }

        // The overwrite halves of the pre-existing case: the recorded
        // previous content still on disk is the pre-state preserved —
        // NotApplied; the expected content imported is Completed.
        [Test]
        public void Reconcile_Overwrite_PreStatePreserved_ReturnsNotApplied()
        {
            var path = ProbeFolder + "/OverwriteKept.cs";
            File.WriteAllText(PhysicalPath(path), "// first version\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var snapshot = PreflightSnapshot(Inputs(path, OverwriteSource, overwrite: true));

            var verdict = Op().Reconcile(
                Context(Inputs(path, OverwriteSource, overwrite: true)), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>(),
                "the previous bytes still on disk prove the overwrite did not run");
        }

        [Test]
        public void Reconcile_Overwrite_AppliedState_ReturnsCompleted()
        {
            var path = ProbeFolder + "/OverwriteDone.cs";
            File.WriteAllText(PhysicalPath(path), "// first version\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var inputs = Inputs(path, OverwriteSource, overwrite: true);
            var snapshot = PreflightSnapshot(inputs);
            Assert.That(Op().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict = Op().Reconcile(Context(inputs), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>());
        }

        // The overwrite boundary the §F5 evidence cannot decide: when the
        // recorded pre-state already held the requested bytes, the on-disk
        // match cannot distinguish "the overwrite ran" from "the step never
        // ran" — a pre-existing artifact is never claimed as the step's
        // product, so the verdict is Unknown and the file stays untouched.
        [Test]
        public void Reconcile_Overwrite_IdenticalPreState_ReturnsUnknown()
        {
            var path = ProbeFolder + "/SameBytes.cs";
            File.WriteAllText(PhysicalPath(path), OverwriteSource);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var inputs = Inputs(path, OverwriteSource, overwrite: true);
            var snapshot = PreflightSnapshot(inputs);

            var verdict = Op().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "cannot prove");
            Assert.That(File.ReadAllText(PhysicalPath(path)),
                Is.EqualTo(OverwriteSource),
                "an indistinguishable artifact is left untouched");
        }

        // Case 5 — the artifact was changed by another operation after the
        // step ran: content no longer matches the expectation.
        [Test]
        public void Reconcile_ModifiedArtifact_ReturnsUnknown_AndDeletesNothing()
        {
            var path = ProbeFolder + "/ModifiedProbe.cs";
            var inputs = Inputs(path, ProbeSource);
            var snapshot = PreflightSnapshot(inputs);
            Assert.That(Op().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            File.WriteAllText(PhysicalPath(path), "// modified by another operation\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var verdict = Op().Reconcile(Context(inputs), snapshot);

            AssertUnknown(verdict, "matches neither");
            Assert.That(File.ReadAllText(PhysicalPath(path)),
                Is.EqualTo("// modified by another operation\n"),
                "a modified artifact is left untouched");
            Assert.That(
                Directory.GetFiles(PhysicalPath(ProbeFolder), "*.cs"), Has.Length.EqualTo(1),
                "no duplicate artifact was created");
        }

        // Case 6 — a simulated Editor restart (different editorSessionId,
        // discarded handles): the file artifact stays re-identifiable by
        // its permanent keys, so the verdict still follows the evidence —
        // Completed when the artifact is whole, NotApplied when absent
        // (§F5 common rule 3).
        [Test]
        public void Reconcile_SimulatedRestart_StillDecidesByEvidence()
        {
            var path = ProbeFolder + "/RestartProbe.cs";
            var inputs = Inputs(path, ProbeSource);
            var snapshot = PreflightSnapshot(inputs);
            Assert.That(Op().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var restarted = (JObject)snapshot.ToToken();
            restarted["editorSessionId"] = "00000000000000000000000000c0ffee";
            var restartSnapshot = JsonValue.From(restarted);

            var verdict = Op().Reconcile(Context(inputs), restartSnapshot);
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "a file artifact is re-identifiable across a session boundary");

            Assert.That(AssetDatabase.DeleteAsset(path), Is.True,
                "the fixture must delete the imported probe asset");
            AssetDatabase.Refresh();
            Assert.That(File.Exists(PhysicalPath(path)), Is.False);
            Assert.That(File.Exists(PhysicalPath(path) + ".meta"), Is.False);
            Assert.That(AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets), Is.Empty);
            var absent = Op().Reconcile(Context(inputs), restartSnapshot);
            Assert.That(absent, Is.TypeOf<ReconcileResult.NotApplied>(),
                "an absent artifact stays safely re-runnable after a restart");
        }

        // The leftover-occupant cases: NotApplied needs a truly empty
        // target (§F5); a file-less .meta or a database entry the refresh
        // never cleaned occupy the path — Unknown, never re-run.
        [Test]
        public void Reconcile_OrphanMetaWithoutFile_ReturnsUnknown()
        {
            var path = ProbeFolder + "/OrphanMeta.cs";
            var inputs = Inputs(path, ProbeSource);
            var snapshot = PreflightSnapshot(inputs);
            Assert.That(Op().Execute(Context(inputs)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            File.Delete(PhysicalPath(path));
            Assert.That(File.Exists(PhysicalPath(path)), Is.False);
            Assert.That(File.Exists(PhysicalPath(path) + ".meta"), Is.True,
                "without a refresh the .meta outlives the file");

            AssertUnknown(Op().Reconcile(Context(inputs), snapshot), "without a file");
        }

        // Evidence that cannot be re-established is Unknown — never
        // Completed, never NotApplied.
        [Test]
        public void Reconcile_MalformedOrUnestablishedEvidence_ReturnsUnknown()
        {
            var path = ProbeFolder + "/EvidenceProbe.cs";
            var snapshot = PreflightSnapshot(Inputs(path, ProbeSource));

            Assert.That(
                Op().Reconcile(Context(Inputs(path, ProbeSource)),
                    JsonValue.From(new JValue(5))),
                Is.TypeOf<ReconcileResult.Unknown>(),
                "a non-object snapshot restores no evidence");

            var missing = (JObject)snapshot.ToToken();
            missing.Remove("expectedContentHash");
            AssertUnknown(
                Op().Reconcile(Context(Inputs(path, ProbeSource)), JsonValue.From(missing)),
                "missing");

            var badHash = (JObject)snapshot.ToToken();
            badHash["expectedContentHash"] = "not-hex";
            AssertUnknown(
                Op().Reconcile(Context(Inputs(path, ProbeSource)), JsonValue.From(badHash)),
                "malformed");
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
                throw new NotSupportedException("path checks read Parameters only");
        }

        private static StaticContext Static(IReadOnlyDictionary<string, InputValue> parameters) =>
            new StubStaticContext(parameters);

        private static Dictionary<string, InputValue> Inputs(
            string path, string content, bool overwrite = false)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["path"] = InputValue.Present(Json("\"" + path + "\"")),
                ["content"] = InputValue.Present(
                    JsonValue.From(JToken.FromObject(content))),
            };
            if (overwrite)
            {
                inputs["overwrite"] = InputValue.Present(Json("true"));
            }
            return inputs;
        }

        private static JsonValue PreflightSnapshot(IReadOnlyDictionary<string, InputValue> inputs)
        {
            var preflight = Op().Preflight(Context(inputs));
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => "preflight must produce the snapshot: " + string.Join("; ",
                    preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return preflight.Snapshot;
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IChangeRecorder? changes = null,
            JsonValue? resumeToken = null) =>
            new UnityStepContext(
                inputs,
                InputTypes,
                OutputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                changes ?? new StepChangeRecorder(0, "script"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                resumeToken);

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

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }
    }
}
