#nullable enable

// T-W2.1-02 [Edit] tests: local:T-W2.1-02:requirement-evaluators — the
// SPEC §G1 item 4 Requirement evaluators (edit_mode, not_compiling,
// scene_loaded, path_writable, type_exists) in satisfying and violating
// Editor states. Seam fixtures drive each state; live-binding tests check
// the real Editor, package manager and file system.
// T-W2.7-05 adds the existing-directory destination rows of
// path_writable: a folder destination is satisfied when files can be
// created in the directory and OW-UNITY-010 when they cannot; a file
// destination is never writable where a directory stands.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class EditorRequirementEvaluatorTests
    {
        // ── Test doubles ─────────────────────────────────────────────────

        private sealed class FakeEditorState : IEditorState
        {
            public bool IsEditMode { get; set; } = true;
            public bool IsCompiling { get; set; }
            public bool IsUpdating { get; set; }
        }

        private sealed class FakeTypeProbe : IStaticTypeProbe
        {
            private readonly HashSet<string> _known = new HashSet<string>(StringComparer.Ordinal);
            public void Know(string name) => _known.Add(name);
            public bool TypeExists(string typeName) => _known.Contains(typeName);
        }

        private sealed class Env
        {
            public readonly FakeEditorState State = new FakeEditorState();
            public readonly FakeTypeProbe Probe = new FakeTypeProbe();
            public List<string> Allowed = new List<string> { "Assets/" };
            public readonly Dictionary<string, string> GuidPaths =
                new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly HashSet<string> LoadedScenes =
                new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Immutable =
                new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> ReadOnlyOnDisk =
                new HashSet<string>(StringComparer.Ordinal);

            public EditorRequirementEvaluator Evaluator() =>
                new EditorRequirementEvaluator(
                    State,
                    Probe,
                    Allowed,
                    guid => GuidPaths.TryGetValue(guid, out var path) ? path : "",
                    path => LoadedScenes.Contains(path),
                    path => Immutable.Contains(path),
                    (path, _) => ReadOnlyOnDisk.Contains(path));

            public RequirementResult EvaluateAll(
                EditorRequirementEvaluator evaluator,
                IReadOnlyDictionary<string, JsonValue?> values,
                params Requirement[] requirements) =>
                evaluator.EvaluateAll(
                    requirements,
                    port => values.TryGetValue(port, out var value) ? value : null,
                    "node1");
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"fixture JSON must parse: {text}");
            return parsed.Value!;
        }

        private static JsonValue SceneRef(string? path = null, string? guid = null)
        {
            var obj = new JObject { ["kind"] = "scene" };
            if (path is not null)
            {
                obj["path"] = path;
            }
            if (guid is not null)
            {
                obj["guid"] = guid;
            }
            return JsonValue.From(obj);
        }

        private static void AssertSatisfied(RequirementResult result)
        {
            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Satisfied));
            Assert.That(result.Diagnostic, Is.Null);
        }

        // ── edit_mode ────────────────────────────────────────────────────

        [Test]
        public void EditMode_SatisfiedInEditMode_ViolationWhenPlaying()
        {
            var env = new Env();
            var evaluator = env.Evaluator();
            var requirement = new Requirement("edit_mode");

            env.State.IsEditMode = true;
            AssertSatisfied(evaluator.Evaluate(requirement, null, "node1"));

            env.State.IsEditMode = false;
            var result = evaluator.Evaluate(requirement, null, "node1");
            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Violated));
            var diagnostic = result.Diagnostic;
            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_RUN_001));
            Assert.That(diagnostic.Severity, Is.EqualTo(Severity.Error));
            Assert.That(diagnostic.NodeId, Is.EqualTo("node1"));
        }

        // ── not_compiling ────────────────────────────────────────────────

        [Test]
        public void NotCompiling_SatisfiedWhenIdle_WaitsWhileCompiling()
        {
            var env = new Env();
            var evaluator = env.Evaluator();
            var requirement = new Requirement("not_compiling");

            env.State.IsCompiling = false;
            AssertSatisfied(evaluator.Evaluate(requirement, null));

            // The 2026-11-06 owner decision (SPEC §1.4, §E4 step 2, §G1 item
            // 4): while the Editor compiles the requirement is "not
            // satisfied, wait" — a Wait verdict carrying no error code and
            // no diagnostic, never OW-RUN-001; after compilation it is
            // satisfied.
            env.State.IsCompiling = true;
            var waiting = evaluator.Evaluate(requirement, null);
            Assert.That(waiting.Verdict, Is.EqualTo(RequirementVerdict.Wait));
            Assert.That(waiting.Diagnostic, Is.Null);
        }

        // ── scene_loaded ─────────────────────────────────────────────────

        [Test]
        public void SceneLoaded_SatisfiedWhenReferencedSceneIsLoaded()
        {
            var env = new Env();
            env.LoadedScenes.Add("Assets/Scenes/main.unity");

            AssertSatisfied(env.Evaluator().Evaluate(
                new Requirement("scene_loaded", "scene"),
                SceneRef(path: "Assets/Scenes/main.unity")));
        }

        [Test]
        public void SceneLoaded_ViolationWhenSceneNotLoaded()
        {
            var env = new Env();

            var result = env.Evaluator().Evaluate(
                new Requirement("scene_loaded", "scene"),
                SceneRef(path: "Assets/Scenes/absent.unity"));

            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Violated));
            var diagnostic = result.Diagnostic;
            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_UNITY_003));
            Assert.That(diagnostic.Port, Is.EqualTo("scene"));
        }

        [Test]
        public void SceneLoaded_GuidResolvesToLoadedPathFirst()
        {
            var env = new Env();
            env.GuidPaths["guid-1"] = "Assets/Scenes/from-guid.unity";
            env.LoadedScenes.Add("Assets/Scenes/from-guid.unity");

            // guid wins the §G2 candidate order: satisfied via the resolved
            // path even though the path key points at a scene not loaded.
            AssertSatisfied(env.Evaluator().Evaluate(
                new Requirement("scene_loaded", "scene"),
                SceneRef(path: "Assets/Scenes/other.unity", guid: "guid-1")));
        }

        [Test]
        public void SceneLoaded_GuidUnresolvedFallsBackToPath()
        {
            var env = new Env();
            env.LoadedScenes.Add("Assets/Scenes/main.unity");

            AssertSatisfied(env.Evaluator().Evaluate(
                new Requirement("scene_loaded", "scene"),
                SceneRef(path: "Assets/Scenes/main.unity", guid: "guid-gone")));
        }

        [Test]
        public void SceneLoaded_ViolationWhenNothingIdentifiesTheScene()
        {
            var env = new Env();
            var evaluator = env.Evaluator();
            var requirement = new Requirement("scene_loaded", "scene");

            // A Ref carrying neither guid nor path names no loaded scene.
            Assert.That(
                evaluator.Evaluate(requirement, SceneRef()).Diagnostic!.Code,
                Is.EqualTo(ErrorCodes.OW_UNITY_003));
            // A bare path string is a scene path too (the 'scene' key of a
            // GameObjectRef may be one): not loaded, then loaded.
            var barePath = Json("\"Assets/Scenes/x.unity\"");
            Assert.That(
                evaluator.Evaluate(requirement, barePath).Diagnostic!.Code,
                Is.EqualTo(ErrorCodes.OW_UNITY_003));
            env.LoadedScenes.Add("Assets/Scenes/x.unity");
            AssertSatisfied(evaluator.Evaluate(requirement, barePath));
        }

        [Test]
        public void SceneLoaded_AbsentPortValueIsSatisfied()
        {
            var env = new Env();
            var requirement = new Requirement("scene_loaded", "scene");

            AssertSatisfied(env.Evaluator().Evaluate(requirement, null));
            AssertSatisfied(env.Evaluator().Evaluate(requirement, Json("null")));
        }

        // ── path_writable ────────────────────────────────────────────────

        [Test]
        public void PathWritable_SatisfiedInsideAllowedPaths()
        {
            var env = new Env();
            AssertSatisfied(env.Evaluator().Evaluate(
                new Requirement("path_writable", "path"), Json("\"Assets/Scripts/a.cs\"")));
        }

        [Test]
        public void PathWritable_ViolationOutsideAllowedPaths()
        {
            var env = new Env();

            var diagnostic = env.Evaluator().Evaluate(
                new Requirement("path_writable", "path"), Json("\"Packages/com.x/a.cs\"")).Diagnostic;

            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
        }

        [Test]
        public void PathWritable_RejectsTraversalRootedAndAbsolutePaths()
        {
            var env = new Env();
            var evaluator = env.Evaluator();
            var requirement = new Requirement("path_writable", "path");

            foreach (var bad in new[]
            {
                "\"Assets/../ProjectSettings/x\"",
                "\"../Assets/x\"",
                "\"/Assets/x\"",
                "\"C:/Assets/x\"",
                "\"~/Assets/x\"",
            })
            {
                var diagnostic = evaluator.Evaluate(requirement, Json(bad)).Diagnostic;
                Assert.That(diagnostic, Is.Not.Null, bad);
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_002), bad);
            }
        }

        [Test]
        public void PathWritable_NormalizesSeparatorsAndMatchesCaseInsensitive()
        {
            var env = new Env();
            var evaluator = env.Evaluator();
            var requirement = new Requirement("path_writable", "path");

            // '\' collapses to '/', case-insensitive prefix like Unity paths.
            AssertSatisfied(evaluator.Evaluate(requirement, Json("\"assets\\\\scripts\\\\a.cs\"")));
            AssertSatisfied(evaluator.Evaluate(requirement, Json("\"Assets/./Scripts/a.cs\"")));
            // A sibling directory sharing the prefix text is not inside.
            Assert.That(
                evaluator.Evaluate(requirement, Json("\"AssetsExtra/a.cs\"")).Diagnostic!.Code,
                Is.EqualTo(ErrorCodes.OW_PERM_002));
        }

        [Test]
        public void PathWritable_ImmutablePackageInsideAllowedIsOwUnity010()
        {
            var env = new Env();
            env.Allowed = new List<string> { "Assets/", "Packages/" };
            env.Immutable.Add("Packages/com.unity.test-framework/package.json");

            var diagnostic = env.Evaluator().Evaluate(
                new Requirement("path_writable", "path"),
                Json("\"Packages/com.unity.test-framework/package.json\"")).Diagnostic;

            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_UNITY_010));
        }

        [Test]
        public void PathWritable_AllowedCheckPrecedesReadOnlyCheck()
        {
            var env = new Env();
            // Immutable AND outside the allowed paths: OW-PERM-002 wins.
            env.Immutable.Add("Packages/com.x/a.cs");

            var diagnostic = env.Evaluator().Evaluate(
                new Requirement("path_writable", "path"), Json("\"Packages/com.x/a.cs\"")).Diagnostic;

            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
        }

        [Test]
        public void PathWritable_ReadOnlyFileInsideAllowedIsOwUnity010()
        {
            var env = new Env();
            env.ReadOnlyOnDisk.Add("Assets/locked.asset");

            var diagnostic = env.Evaluator().Evaluate(
                new Requirement("path_writable", "path"), Json("\"Assets/locked.asset\"")).Diagnostic;

            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_UNITY_010));
        }

        [Test]
        public void PathWritable_EmptyAllowlistDeniesEverything()
        {
            var env = new Env();
            env.Allowed = new List<string>();

            var diagnostic = env.Evaluator().Evaluate(
                new Requirement("path_writable", "path"), Json("\"Assets/a.cs\"")).Diagnostic;

            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
        }

        // SPEC §H4 item 5 (T-W3.4-03): the OpWeave settings file is never
        // part of the permitted write paths — refused with OW-PERM-002 under
        // the default allowedPaths and under a set widened to cover it, for
        // every spelling that reaches the same file on Windows.
        [Test]
        public void PathWritable_SettingsFileRefusedEvenWhenAllowed()
        {
            var env = new Env();
            // Widened past the settings folder to the project root: '.' is the
            // match-all prefix, so every row below is inside the allowed paths
            // and only §H4 item 5 can refuse it.
            env.Allowed = new List<string> { "Assets/", "ProjectSettings/", "." };
            var evaluator = env.Evaluator();
            var requirement = new Requirement("path_writable", "path");

            foreach (var json in new[]
            {
                "\"ProjectSettings/OpWeaveSettings.json\"",
                "\"projectsettings/opweavesettings.json\"",
                "\"ProjectSettings\\\\OpWeaveSettings.json\"",
                "\"ProjectSettings/./OpWeaveSettings.json\"",
                "\"ProjectSettings/OpWeaveSettings.json.\"",
                "\"ProjectSettings/OpWeaveSettings.json \"",
                "\"ProjectSettings./OpWeaveSettings.json\"",
                "\"ProjectSettings/OpWeaveSettings.json::$DATA\"",
                "\"projectsettings\\\\OpWeaveSettings.JSON . \"",
                "\"ProjectSettings/OPWEAV~1.JSO\"",
                "\"ProjectSettings/OPWEA~10.JSO\"",
                "\"ProjectSettings/OP1F2A~1.JSO\"",
                "\"PROJEC~1/OpWeaveSettings.json\"",
                "\"PROJE~10/OpWeaveSettings.json\"",
                "\"PROJ~100/OpWeaveSettings.json\"",
                "\"PR1A2B~1/OpWeaveSettings.json\"",
                "\"PROJEC~1/OPWEAV~1.JSO\"",
                "\"projec~1\\\\OpWeaveSettings.json.\"",
            })
            {
                var diagnostic = evaluator.Evaluate(requirement, Json(json)).Diagnostic;
                Assert.That(diagnostic, Is.Not.Null, json);
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_002), json);
            }

            // The default allowedPaths (Assets/ only) refuses it too — as
            // any path outside the allowed set, never a different code.
            var defaultDiagnostic = new Env().Evaluator()
                .Evaluate(requirement, Json("\"ProjectSettings/OpWeaveSettings.json\""))
                .Diagnostic;
            Assert.That(defaultDiagnostic, Is.Not.Null);
            Assert.That(defaultDiagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
        }

        // The protection is the settings file, not the folder: a neighbour
        // path inside an allowed ProjectSettings/ prefix still passes, and so
        // do the same file name and a short-name-looking name under Assets/. A
        // '~<digits>' name only counts when Windows could derive it from the
        // guarded name, so an unrelated 'Data~1'-style folder the settings
        // admit stays writable and a name too long to be an alias is not the
        // settings file either.
        [Test]
        public void PathWritable_OnlyTheSettingsFileItselfIsProtected()
        {
            var env = new Env();
            env.Allowed = new List<string> { "Assets/", "ProjectSettings/", "Data~1/", "Assets~1/" };
            var evaluator = env.Evaluator();
            var requirement = new Requirement("path_writable", "path");

            AssertSatisfied(evaluator.Evaluate(
                requirement, Json("\"ProjectSettings/OtherSettings.json\"")));
            AssertSatisfied(evaluator.Evaluate(
                requirement, Json("\"ProjectSettings/OpWeaveSettings.jsonx\"")));
            AssertSatisfied(evaluator.Evaluate(
                requirement, Json("\"ProjectSettings/OpWeaveSettings~1.json\"")));
            AssertSatisfied(evaluator.Evaluate(
                requirement, Json("\"ProjectSettings/Foo~1.txt\"")));
            AssertSatisfied(evaluator.Evaluate(
                requirement, Json("\"Assets/OpWeaveSettings.json\"")));
            AssertSatisfied(evaluator.Evaluate(
                requirement, Json("\"Assets/Foo~1.txt\"")));
            AssertSatisfied(evaluator.Evaluate(
                requirement, Json("\"Data~1/output.json\"")));
            AssertSatisfied(evaluator.Evaluate(
                requirement, Json("\"Assets~1/x.txt\"")));
        }

        [Test]
        public void PathWritable_AbsentPortValueIsSatisfied()
        {
            var env = new Env();
            var requirement = new Requirement("path_writable", "path");

            AssertSatisfied(env.Evaluator().Evaluate(requirement, null));
            AssertSatisfied(env.Evaluator().Evaluate(requirement, Json("null")));
        }

        // ── type_exists ──────────────────────────────────────────────────

        [Test]
        public void TypeExists_SatisfiedForKnownType_ViolationForUnknown()
        {
            var env = new Env();
            env.Probe.Know("UnityEngine.Animator");
            var evaluator = env.Evaluator();
            var requirement = new Requirement("type_exists", "componentType");

            AssertSatisfied(evaluator.Evaluate(requirement, Json("\"UnityEngine.Animator\"")));

            var diagnostic = evaluator.Evaluate(requirement, Json("\"Missing.Type\"")).Diagnostic;
            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_UNITY_006));
            Assert.That(diagnostic.Port, Is.EqualTo("componentType"));
        }

        [Test]
        public void TypeExists_AbsentPortValueIsSatisfied()
        {
            var env = new Env();
            var requirement = new Requirement("type_exists", "type");

            AssertSatisfied(env.Evaluator().Evaluate(requirement, null));
            AssertSatisfied(env.Evaluator().Evaluate(requirement, Json("null")));
        }

        // ── Cross-kind behaviour ─────────────────────────────────────────

        [Test]
        public void UnknownKindFailsClosedAsInvalidDescriptor()
        {
            var env = new Env();

            var result = env.Evaluator().Evaluate(
                new Requirement("custom_unknown_kind"), null);

            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Violated));
            var diagnostic = result.Diagnostic;
            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_OP_003));
        }

        [Test]
        public void EvaluateAll_ReportsFirstViolationInDeclaredOrder()
        {
            var env = new Env();
            env.LoadedScenes.Add("Assets/Scenes/main.unity");
            var values = new Dictionary<string, JsonValue?>
            {
                ["scene"] = SceneRef(path: "Assets/Scenes/main.unity"),
                ["path"] = Json("\"Packages/com.x/a.cs\""),
            };

            // Declared order wins: edit_mode violated reports OW-RUN-001 even
            // though path_writable would also fail.
            env.State.IsEditMode = false;
            var result = env.EvaluateAll(
                env.Evaluator(),
                values,
                new Requirement("edit_mode"),
                new Requirement("scene_loaded", "scene"),
                new Requirement("path_writable", "path"));
            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Violated));
            var diagnostic = result.Diagnostic;
            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_RUN_001));

            // With the environment satisfied the port-bound order decides.
            env.State.IsEditMode = true;
            result = env.EvaluateAll(
                env.Evaluator(),
                values,
                new Requirement("edit_mode"),
                new Requirement("scene_loaded", "scene"),
                new Requirement("path_writable", "path"));
            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Violated));
            diagnostic = result.Diagnostic;
            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
            Assert.That(diagnostic.NodeId, Is.EqualTo("node1"));
        }

        [Test]
        public void EvaluateAll_NotCompilingWaitShortCircuitsLaterRequirements()
        {
            var env = new Env();
            env.State.IsCompiling = true;
            var evaluator = env.Evaluator();
            var values = new Dictionary<string, JsonValue?>
            {
                ["path"] = Json("\"Packages/com.x/a.cs\""),
                ["componentType"] = Json("\"Still.Compiling.Type\""),
            };

            // SPEC §E4 step 2 (the 2026-11-06 amendment): while the Editor
            // compiles, not_compiling's Wait ends the Tick's processing
            // for the step — no diagnostic, retried from E4 step 1 on the
            // next Tick — so requirements declared after it are not
            // evaluated. The portValue seam records every port lookup:
            // neither path_writable's nor type_exists's port is resolved,
            // so a still-compiling type cannot draw a spurious
            // OW-UNITY-006.
            var probed = new List<string>();
            var result = evaluator.EvaluateAll(
                new Requirement[]
                {
                    new Requirement("not_compiling"),
                    new Requirement("path_writable", "path"),
                    new Requirement("type_exists", "componentType"),
                },
                port =>
                {
                    probed.Add(port);
                    return values.TryGetValue(port, out var value) ? value : null;
                },
                "node1");
            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Wait));
            Assert.That(result.Diagnostic, Is.Null);
            Assert.That(probed, Is.Empty);

            // A violation declared before not_compiling still reports its
            // diagnostic first, in declared order.
            result = env.EvaluateAll(
                evaluator,
                values,
                new Requirement("edit_mode"),
                new Requirement("path_writable", "path"),
                new Requirement("not_compiling"),
                new Requirement("type_exists", "componentType"));
            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Violated));
            Assert.That(result.Diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
        }

        [Test]
        public void EvaluateAll_SatisfiedWhenEveryRequirementPasses()
        {
            var env = new Env();
            env.LoadedScenes.Add("Assets/Scenes/main.unity");
            env.Probe.Know("UnityEngine.Animator");
            var values = new Dictionary<string, JsonValue?>
            {
                ["scene"] = SceneRef(path: "Assets/Scenes/main.unity"),
                ["path"] = Json("\"Assets/Scripts/a.cs\""),
                ["componentType"] = Json("\"UnityEngine.Animator\""),
            };

            // The same Requirement records any Operation — standard or
            // custom — declares evaluate identically (SPEC §G1 item 4).
            AssertSatisfied(env.EvaluateAll(
                env.Evaluator(),
                values,
                new Requirement("edit_mode"),
                new Requirement("not_compiling"),
                new Requirement("scene_loaded", "scene"),
                new Requirement("path_writable", "path"),
                new Requirement("type_exists", "componentType")));
        }

        // ── Live bindings (real Editor state, package manager, file system) ──

        [Test]
        public void LiveBindings_EditModeAndNotCompilingAreSatisfied()
        {
            var evaluator = new EditorRequirementEvaluator(new[] { "Assets/" });

            AssertSatisfied(evaluator.Evaluate(new Requirement("edit_mode"), null));
            AssertSatisfied(evaluator.Evaluate(new Requirement("not_compiling"), null));
        }

        [Test]
        public void TypeProbe_SeamAnswersAndNeverThrows()
        {
            var probe = new UnityTypeProbe(
                name => name == "Known.Type" ? true : throw new InvalidOperationException());

            Assert.IsTrue(probe.TypeExists("Known.Type"));
            Assert.IsFalse(probe.TypeExists("Other.Type"));
            Assert.IsFalse(probe.TypeExists(""));
        }

        [Test]
        public void LiveTypeProbe_FindsLoadedAndEngineTypes()
        {
            var probe = new UnityTypeProbe();

            Assert.IsTrue(probe.TypeExists("UnityEngine.GameObject"));
            Assert.IsTrue(probe.TypeExists("UnityEditor.EditorWindow"));
            Assert.IsTrue(probe.TypeExists("OpWeave.UnityAdapter.EditorStateProbe"));
            Assert.IsFalse(probe.TypeExists("OpWeave.No.Such.Type9"));
            Assert.IsFalse(probe.TypeExists(""));
        }

        [Test]
        public void LiveBindings_TypeExistsMatchesTheProbe()
        {
            var evaluator = new EditorRequirementEvaluator(new[] { "Assets/" });
            var requirement = new Requirement("type_exists", "type");

            AssertSatisfied(evaluator.Evaluate(requirement, Json("\"UnityEngine.GameObject\"")));
            Assert.That(
                evaluator.Evaluate(requirement, Json("\"OpWeave.No.Such.Type9\"")).Diagnostic!.Code,
                Is.EqualTo(ErrorCodes.OW_UNITY_006));
        }

        [Test]
        public void LiveBindings_PathWritableDistinguishesPackageMutability()
        {
            var evaluator = new EditorRequirementEvaluator(new[] { "Packages/" });
            var requirement = new Requirement("path_writable", "path");

            // A registry package is immutable: Packages/ is read-only (§G7.1).
            Assert.That(
                evaluator.Evaluate(
                    requirement,
                    Json("\"Packages/com.unity.test-framework/package.json\"")).Diagnostic!.Code,
                Is.EqualTo(ErrorCodes.OW_UNITY_010));

            // The OpWeave package is a local (file:) source — writable.
            AssertSatisfied(evaluator.Evaluate(
                requirement, Json("\"Packages/com.opweave.workflow/package.json\"")));
        }

        [Test]
        public void LiveBindings_PathWritableFlagsUndeterminablePackageDestination()
        {
            var evaluator = new EditorRequirementEvaluator(new[] { "Packages/" });
            var requirement = new Requirement("path_writable", "path");

            // FindForAssetPath finds no such package, so the physical
            // destination cannot be determined: fail closed — the path
            // counts as not writable even though Packages/ is allowed.
            Assert.That(
                evaluator.Evaluate(
                    requirement,
                    Json("\"Packages/com.opweave.nonexistent.package/file.txt\"")).Diagnostic!.Code,
                Is.EqualTo(ErrorCodes.OW_UNITY_010));
        }

        [Test]
        public void LiveBindings_PathWritableFlagsReadOnlyFiles()
        {
            var probeDir = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "OpWeaveReqEvalProbe");
            var probeFile = Path.Combine(probeDir, "readonly.tmp");
            try
            {
                Directory.CreateDirectory(probeDir);
                File.WriteAllText(probeFile, "probe");
                File.SetAttributes(probeFile, FileAttributes.ReadOnly);

                var evaluator = new EditorRequirementEvaluator(new[] { "Library/" });
                var requirement = new Requirement("path_writable", "path");

                Assert.That(
                    evaluator.Evaluate(
                        requirement,
                        Json("\"Library/OpWeaveReqEvalProbe/readonly.tmp\"")).Diagnostic!.Code,
                    Is.EqualTo(ErrorCodes.OW_UNITY_010));
            }
            finally
            {
                if (File.Exists(probeFile))
                {
                    File.SetAttributes(probeFile, FileAttributes.Normal);
                    File.Delete(probeFile);
                }
                if (Directory.Exists(probeDir))
                {
                    Directory.Delete(probeDir);
                }
            }
        }

        [Test]
        public void LiveBindings_PathWritableSatisfiedForMissingFileInWritableDirectory()
        {
            var probeDir = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "OpWeaveReqEvalProbe");
            var writableDir = Path.Combine(probeDir, "writable");
            try
            {
                Directory.CreateDirectory(writableDir);

                // A not-yet-existing destination under a directory the
                // current user can write to is writable: satisfied.
                var evaluator = new EditorRequirementEvaluator(new[] { "Library/" });
                var requirement = new Requirement("path_writable", "path");

                AssertSatisfied(evaluator.Evaluate(
                    requirement,
                    Json("\"Library/OpWeaveReqEvalProbe/writable/new.tmp\"")));

                // The probe creates and removes one hidden file; nothing
                // may be left behind in the directory.
                Assert.That(
                    Directory.GetFiles(writableDir, ".opweave-write-probe-*"),
                    Is.Empty);
            }
            finally
            {
                if (Directory.Exists(writableDir))
                {
                    Directory.Delete(writableDir);
                }
                if (Directory.Exists(probeDir))
                {
                    Directory.Delete(probeDir);
                }
            }
        }

        [Test]
        public void LiveBindings_PathWritableFlagsFileBlockingAncestor()
        {
            var probeDir = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "OpWeaveReqEvalProbe");
            var blocker = Path.Combine(probeDir, "blocker.tmp");
            try
            {
                Directory.CreateDirectory(probeDir);
                File.WriteAllText(blocker, "probe");

                // A regular file stands where an ancestor directory would
                // have to be: the destination can never be created.
                var evaluator = new EditorRequirementEvaluator(new[] { "Library/" });
                var requirement = new Requirement("path_writable", "path");

                Assert.That(
                    evaluator.Evaluate(
                        requirement,
                        Json("\"Library/OpWeaveReqEvalProbe/blocker.tmp/child.tmp\"")).Diagnostic!.Code,
                    Is.EqualTo(ErrorCodes.OW_UNITY_010));
            }
            finally
            {
                if (File.Exists(blocker))
                {
                    File.Delete(blocker);
                }
                if (Directory.Exists(probeDir))
                {
                    Directory.Delete(probeDir);
                }
            }
        }

        [Test]
        [Platform("Win")]
        public void LiveBindings_PathWritableFlagsDeniedDirectories()
        {
            var probeDir = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "OpWeaveReqEvalProbe");
            var blockedDir = Path.Combine(probeDir, "blocked");
            var identity = Environment.UserDomainName + "\\" + Environment.UserName;
            var denied = false;
            try
            {
                Directory.CreateDirectory(blockedDir);
                if (!TryIcacls($"\"{blockedDir}\" /deny \"{identity}\":(W)", out var reason))
                {
                    Assert.Ignore($"icacls could not deny write on the probe directory: {reason}");
                }
                denied = true;

                // The destination does not exist yet and the nearest
                // existing ancestor directory denies the current user
                // write access: the probe file cannot be created — not
                // writable.
                var evaluator = new EditorRequirementEvaluator(new[] { "Library/" });
                var requirement = new Requirement("path_writable", "path");

                Assert.That(
                    evaluator.Evaluate(
                        requirement,
                        Json("\"Library/OpWeaveReqEvalProbe/blocked/child.tmp\"")).Diagnostic!.Code,
                    Is.EqualTo(ErrorCodes.OW_UNITY_010));
            }
            finally
            {
                if (denied)
                {
                    TryIcacls($"\"{blockedDir}\" /remove:d \"{identity}\"", out _);
                }
                if (Directory.Exists(blockedDir))
                {
                    Directory.Delete(blockedDir);
                }
                if (Directory.Exists(probeDir))
                {
                    Directory.Delete(probeDir);
                }
            }
        }

        // T-W2.7-05: an existing directory answers by the kind of
        // destination the requirement's port declares. A folder
        // destination — the (Operation, port) pairs of create_folder's
        // 'path' and create_from_template's 'folder' — is writable when
        // the current user can create files in it, the same
        // effective-write probe a missing path applies to its nearest
        // ancestor; this is what create_folder's "existing folder is a
        // success" (SPEC §G7 item 3, §I.3 "既存なら成功") needs. A file
        // destination on the same directory stays OW-UNITY-010 — the
        // refusal every file-writing path_writable user kept on main —
        // and an evaluation that cannot name its Operation keeps the
        // file-destination reading.
        [Test]
        public void LiveBindings_PathWritableExistingDirectoryAnswersByDestinationKind()
        {
            var probeDir = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "OpWeaveReqEvalProbe");
            var existingDir = Path.Combine(probeDir, "existing");
            try
            {
                Directory.CreateDirectory(existingDir);

                var evaluator = new EditorRequirementEvaluator(new[] { "Library/" });
                var requirement = new Requirement("path_writable", "path");
                var json = Json("\"Library/OpWeaveReqEvalProbe/existing\"");

                // The folder-destination pairs are satisfied.
                AssertSatisfied(evaluator.Evaluate(
                    requirement, json, "node1", "unity.asset.create_folder"));
                AssertSatisfied(evaluator.Evaluate(
                    new Requirement("path_writable", "folder"),
                    json, "node1", "unity.script.create_from_template"));

                // A file destination on the same directory is not
                // writable — the same path under a file-writing
                // Operation, the folder Operation's id with a port that
                // is not its folder destination, and an evaluation that
                // names no Operation all keep the file-destination
                // reading.
                Assert.That(
                    evaluator.Evaluate(
                        requirement, json, "node1", "unity.asset.create_material")
                        .Diagnostic!.Code,
                    Is.EqualTo(ErrorCodes.OW_UNITY_010));
                Assert.That(
                    evaluator.Evaluate(
                        requirement, json, "node1", "unity.script.create_from_template")
                        .Diagnostic!.Code,
                    Is.EqualTo(ErrorCodes.OW_UNITY_010));
                Assert.That(
                    evaluator.Evaluate(requirement, json).Diagnostic!.Code,
                    Is.EqualTo(ErrorCodes.OW_UNITY_010));

                // The probe creates and removes one hidden file; nothing
                // may be left behind in the directory.
                Assert.That(
                    Directory.GetFiles(existingDir, ".opweave-write-probe-*"),
                    Is.Empty);
            }
            finally
            {
                if (Directory.Exists(existingDir))
                {
                    Directory.Delete(existingDir);
                }
                if (Directory.Exists(probeDir))
                {
                    Directory.Delete(probeDir);
                }
            }
        }

        // The refusal half of the same branch: an existing directory the
        // current user cannot write to is OW-UNITY-010 even under the
        // folder-destination reading — a folder the step cannot create
        // into is not a success for the rerun or for the create.
        [Test]
        [Platform("Win")]
        public void LiveBindings_PathWritableFlagsDeniedExistingDirectory()
        {
            var probeDir = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "OpWeaveReqEvalProbe");
            var blockedDir = Path.Combine(probeDir, "denied");
            var identity = Environment.UserDomainName + "\\" + Environment.UserName;
            var denied = false;
            try
            {
                Directory.CreateDirectory(blockedDir);
                if (!TryIcacls($"\"{blockedDir}\" /deny \"{identity}\":(W)", out var reason))
                {
                    Assert.Ignore(
                        $"icacls could not deny write on the probe directory: {reason}");
                }
                denied = true;

                var evaluator = new EditorRequirementEvaluator(new[] { "Library/" });
                var requirement = new Requirement("path_writable", "path");

                Assert.That(
                    evaluator.Evaluate(
                        requirement,
                        Json("\"Library/OpWeaveReqEvalProbe/denied\""),
                        "node1",
                        "unity.asset.create_folder").Diagnostic!.Code,
                    Is.EqualTo(ErrorCodes.OW_UNITY_010));
            }
            finally
            {
                if (denied)
                {
                    TryIcacls($"\"{blockedDir}\" /remove:d \"{identity}\"", out _);
                }
                if (Directory.Exists(blockedDir))
                {
                    Directory.Delete(blockedDir);
                }
                if (Directory.Exists(probeDir))
                {
                    Directory.Delete(probeDir);
                }
            }
        }

        // Runs icacls synchronously and reports whether it exited
        // successfully; false — with the captured output or the launch
        // failure as <paramref name="output"/> — when icacls is
        // unavailable or fails.
        private static bool TryIcacls(string arguments, out string output)
        {
            try
            {
                var info = new ProcessStartInfo("icacls", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var process = Process.Start(info))
                {
                    if (process is null)
                    {
                        output = "icacls did not start";
                        return false;
                    }
                    output = process.StandardOutput.ReadToEnd() +
                        process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception e)
            {
                output = e.Message;
                return false;
            }
        }

        private const string SceneProbeFolder = "Assets/OpWeaveReqEvalProbe";
        private const string SceneProbePath = "Assets/OpWeaveReqEvalProbe/probe.unity";
        private const string SceneSingleProbePath = "Assets/OpWeaveReqEvalProbe/single.unity";

        [Test]
        public void LiveBindings_SceneLoadedChecksTheRealSceneTable()
        {
            var evaluator = new EditorRequirementEvaluator(new[] { "Assets/" });
            var requirement = new Requirement("scene_loaded", "scene");

            // The test must not depend on the Editor's initial scene set or
            // on test order: an earlier test may leave an untitled unsaved
            // scene open, and NewScene(Additive) refuses to run next to one.
            // Save the current setup so it can be restored afterwards.
            var savedSetup = EditorSceneManager.GetSceneManagerSetup();
            UnityEngine.SceneManagement.Scene scene = default;
            try
            {
                // A clean single scene is the known starting point; it also
                // clears any inherited untitled scene before the additive
                // step below.
                var single = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Single);

                if (!AssetDatabase.IsValidFolder(SceneProbeFolder))
                {
                    AssetDatabase.CreateFolder("Assets", "OpWeaveReqEvalProbe");
                }
                // The replacement single scene is itself untitled and
                // unsaved; NewScene(Additive) is refused while one is open,
                // so it must be saved to its own temp asset first.
                Assert.IsTrue(
                    EditorSceneManager.SaveScene(single, SceneSingleProbePath),
                    "the single probe scene must save");

                scene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                Assert.IsTrue(
                    EditorSceneManager.SaveScene(scene, SceneProbePath),
                    "the probe scene must save");
                Assert.That(scene.path, Is.EqualTo(SceneProbePath));

                // The saved scene is open and loaded: path and guid resolve.
                AssertSatisfied(evaluator.Evaluate(requirement, SceneRef(path: SceneProbePath)));
                var guid = AssetDatabase.AssetPathToGUID(SceneProbePath);
                Assert.That(guid, Is.Not.Null.And.Not.EqualTo(""));
                AssertSatisfied(evaluator.Evaluate(requirement, SceneRef(guid: guid)));

                // A scene path with nothing open is OW-UNITY-003.
                Assert.That(
                    evaluator.Evaluate(
                        requirement,
                        SceneRef(path: "Assets/OpWeaveReqEvalProbe/not-open.unity")).Diagnostic!.Code,
                    Is.EqualTo(ErrorCodes.OW_UNITY_003));
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
                // Put the Editor's scene set back the way the test runner had
                // it; fall back to a fresh empty scene when nothing was open.
                if (savedSetup != null && savedSetup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(savedSetup);
                }
                else
                {
                    EditorSceneManager.NewScene(
                        NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                if (AssetDatabase.IsValidFolder(SceneProbeFolder))
                {
                    AssetDatabase.DeleteAsset(SceneProbeFolder);
                }
                AssetDatabase.Refresh();
            }
        }
    }
}
