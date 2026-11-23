#nullable enable

// T-W2.7-01 [Edit] tests: the SPEC §G7 item 1 asset path policy of
// OpWeave.Operations — '/'-separated normalization, the OW-PERM-002 refusal of
// '..' and of rooted/absolute/home spellings, the OW-UNITY-010 refusal of a
// write into a read-only package with the allowed/path-shape check first, and
// the read-only-package contents staying readable. The allowed-paths half of
// the same clause is the declared path_writable requirement, so the last two
// tests judge that declaration through the real §G1 evaluator with the real
// package manager behind it (SPEC §G1 item 4, §G7 item 1, §I.3).
using System;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Operations;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The asset path policy fixtures of <c>local:T-W2.7-01:asset-path-simple-ops</c>.
    /// </summary>
    internal sealed class AssetPathPolicyTests
    {
        private const string Port = "path";

        // A path inside a package v1 treats as immutable, derived from the
        // package manager rather than hardcoded so the fixture cannot drift
        // from what this project actually resolves.
        private static string ReadOnlyPackagePath()
        {
            var package = AllPackages().FirstOrDefault(info =>
                info.source != UnityEditor.PackageManager.PackageSource.Embedded &&
                info.source != UnityEditor.PackageManager.PackageSource.Local);
            Assert.That(package, Is.Not.Null,
                "the project must resolve at least one registry, built-in, Git or tarball " +
                "package for the read-only-package fixture to have a subject");
            return package!.assetPath + "/OpWeaveAssetPolicyProbe";
        }

        // A path inside a local (file:) package — a writable project source.
        private static string WritablePackagePath()
        {
            var package = AllPackages().FirstOrDefault(info =>
                info.source == UnityEditor.PackageManager.PackageSource.Local);
            Assert.That(package, Is.Not.Null,
                "the project must resolve at least one local (file:) package for the " +
                "writable-package fixture to have a subject");
            return package!.assetPath + "/Editor";
        }

        private static System.Collections.Generic.IReadOnlyList<
            UnityEditor.PackageManager.PackageInfo> AllPackages()
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            Assert.That(packages, Is.Not.Null.And.Not.Empty,
                "the package manager must answer for this project");
            return packages;
        }

        // ── normalization ──────────────────────────────────────────────────

        [Test]
        public void Normalize_JoinsSeparatorsWithForwardSlashes()
        {
            Assert.That(
                AssetPathPolicy.Normalize("Assets\\Reports\\\\2026/./Q4/"),
                Is.EqualTo("Assets/Reports/2026/Q4"));
            Assert.That(
                AssetPathPolicy.Normalize("Assets/Reports"),
                Is.EqualTo("Assets/Reports"));
        }

        [Test]
        public void Normalize_HasNoFormForPathsLeavingTheProject()
        {
            Assert.That(AssetPathPolicy.Normalize("../Outside"), Is.Null);
            Assert.That(AssetPathPolicy.Normalize("Assets/../Outside"), Is.Null);
            Assert.That(AssetPathPolicy.Normalize("Assets/Reports/../../Outside"), Is.Null);
            Assert.That(AssetPathPolicy.Normalize("/Assets/Reports"), Is.Null);
            Assert.That(AssetPathPolicy.Normalize("\\Assets\\Reports"), Is.Null);
            Assert.That(AssetPathPolicy.Normalize("C:/Assets/Reports"), Is.Null);
            Assert.That(AssetPathPolicy.Normalize("~/Assets/Reports"), Is.Null);
            Assert.That(AssetPathPolicy.Normalize(""), Is.Null);
            Assert.That(AssetPathPolicy.Normalize(null), Is.Null);
            // A path that collapses to nothing keeps no segment; the checks
            // below refuse it because it names no folder either.
            Assert.That(AssetPathPolicy.Normalize("./"), Is.Empty);
        }

        // ── write-path refusals ────────────────────────────────────────────

        [Test]
        public void CheckWritePath_RefusesTraversalAndRootedSpellingsWithOwPerm002()
        {
            foreach (var path in new[]
            {
                "Assets/Reports/../../Outside",
                "../Outside",
                "/Assets/Reports",
                "C:/Assets/Reports",
                "~/Assets/Reports",
                "",
            })
            {
                var result = AssetPathPolicy.CheckWritePath(path, Port);
                Assert.That(result.Ok, Is.False, $"path '{path}' must be refused");
                Assert.That(result.Diagnostics, Has.Count.EqualTo(1));
                Assert.That(result.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
                Assert.That(result.Diagnostics[0].Port, Is.EqualTo(Port));
                Assert.That(
                    result.Diagnostics[0].Message, Does.Not.Contain(".."),
                    "the refused path is never echoed into the diagnostic");
            }
        }

        [Test]
        public void CheckWritePath_RefusesAReadOnlyPackageDestinationWithOwUnity010()
        {
            var path = ReadOnlyPackagePath();

            var result = AssetPathPolicy.CheckWritePath(path, Port);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_UNITY_010));
            Assert.That(result.Diagnostics[0].Port, Is.EqualTo(Port));
        }

        [Test]
        public void CheckWritePath_AllowsProjectAndLocalPackageDestinations()
        {
            Assert.That(
                AssetPathPolicy.CheckWritePath("Assets\\Reports//Q4", Port).Value,
                Is.EqualTo("Assets/Reports/Q4"));
            Assert.That(
                AssetPathPolicy.CheckWritePath(WritablePackagePath(), Port).Ok,
                Is.True,
                "a local (file:) package is a writable project source");
        }

        // SPEC §H4 item 5 (T-W3.4-03): the OpWeave settings file is never a
        // writable destination — every spelling that reaches the same file on
        // Windows is OW-PERM-002 while a ProjectSettings/ neighbour keeps
        // passing.
        [Test]
        public void CheckWritePath_RefusesTheSettingsFileWithOwPerm002()
        {
            // Beyond the case and separator variants: a trailing '.' or space
            // (Windows strips both at the end of a segment), a folder segment
            // ending in '.', the NTFS stream suffix, and an 8.3 alias of the
            // file and of the folder (Windows resolves PROJEC~1 to
            // ProjectSettings, so neither segment reads as the settings path at
            // all). Both alias shapes Windows derives are covered: the classic
            // truncated name with a one-, two- or three-digit tail, and the
            // hashed name a directory switches to once too many collide.
            foreach (var path in new[]
            {
                "ProjectSettings/OpWeaveSettings.json",
                "projectsettings/opweavesettings.json",
                "ProjectSettings\\OpWeaveSettings.json",
                "ProjectSettings/./OpWeaveSettings.json",
                "ProjectSettings/OpWeaveSettings.json.",
                "ProjectSettings/OpWeaveSettings.json ",
                "ProjectSettings./OpWeaveSettings.json",
                "ProjectSettings/OpWeaveSettings.json::$DATA",
                "projectsettings\\OpWeaveSettings.JSON . ",
                "ProjectSettings/OPWEAV~1.JSO",
                "ProjectSettings/OPWEA~10.JSO",
                "ProjectSettings/OP1F2A~1.JSO",
                "PROJEC~1/OpWeaveSettings.json",
                "PROJE~10/OpWeaveSettings.json",
                "PROJ~100/OpWeaveSettings.json",
                "PR1A2B~1/OpWeaveSettings.json",
                "PROJEC~1/OPWEAV~1.JSO",
                "projec~1\\OpWeaveSettings.json.",
            })
            {
                var result = AssetPathPolicy.CheckWritePath(path, Port);
                Assert.That(result.Ok, Is.False, $"path '{path}' must be refused");
                Assert.That(result.Diagnostics, Has.Count.EqualTo(1));
                Assert.That(result.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
                Assert.That(result.Diagnostics[0].Port, Is.EqualTo(Port));
            }

            Assert.That(
                AssetPathPolicy.CheckWritePath("ProjectSettings/OtherSettings.json", Port).Ok,
                Is.True, "the protection is the file, not the folder");
            Assert.That(
                AssetPathPolicy.CheckWritePath("ProjectSettings/OpWeaveSettings.jsonx", Port).Ok,
                Is.True, "a longer file name is not the settings file");
            Assert.That(
                AssetPathPolicy.CheckWritePath("ProjectSettings/OpWeaveSettings~1.json", Port).Ok,
                Is.True, "a name no eight-character alias of the file can spell is not it");
            Assert.That(
                AssetPathPolicy.CheckWritePath("ProjectSettings/Foo~1.txt", Port).Ok,
                Is.True, "a '~<digits>' name inside the folder is not an alias of the file");
            Assert.That(
                AssetPathPolicy.CheckWritePath("Assets/OpWeaveSettings.json", Port).Ok,
                Is.True, "only the settings path itself is protected");
            Assert.That(
                AssetPathPolicy.CheckWritePath("Assets/Foo~1.txt", Port).Ok,
                Is.True, "a short-name shape outside the settings folder is not the settings file");
            Assert.That(
                AssetPathPolicy.CheckWritePath("Data~1/output.json", Port).Ok,
                Is.True, "an unrelated '~<digits>' folder is a folder of the project's own");
            Assert.That(
                AssetPathPolicy.CheckWritePath("Assets~1/x.txt", Port).Ok,
                Is.True, "the same for a '~<digits>' folder that resembles a real one");
        }

        // SPEC §G1 item 4 / §G7 item 1 precedence: a path that cannot stay in
        // the project is refused for its shape even when its first segments
        // already sit inside a read-only package.
        [Test]
        public void CheckWritePath_JudgesPathShapeBeforeTheReadOnlyHalf()
        {
            var escaping = AllPackages()
                .First(info =>
                    info.source != UnityEditor.PackageManager.PackageSource.Embedded &&
                    info.source != UnityEditor.PackageManager.PackageSource.Local)
                .assetPath;
            var path = escaping + "/../Outside";

            var result = AssetPathPolicy.CheckWritePath(path, Port);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
        }

        // ── read paths ─────────────────────────────────────────────────────

        [Test]
        public void CheckReadPath_AllowsPackageContentsButNotTraversal()
        {
            // §G7 item 1: "Packages/ is readable only" — a search may look
            // inside a package, so only the path shape is judged here.
            Assert.That(
                AssetPathPolicy.CheckReadPath(ReadOnlyPackagePath(), Port).Ok,
                Is.True);
            Assert.That(
                AssetPathPolicy.CheckReadPath("Packages/com.opweave.workflow/Editor", Port).Ok,
                Is.True);
            Assert.That(
                AssetPathPolicy.CheckReadPath("Assets/../Outside", Port).Ok,
                Is.False);
            Assert.That(
                AssetPathPolicy.CheckReadPath("Assets/../Outside", Port)
                    .Diagnostics[0].Code,
                Is.EqualTo(ErrorCodes.OW_PERM_002));
        }

        // ── the allowed-paths half, through the declared requirement ───────

        // The §I.3 write port of unity.asset.create_folder carries the §G1
        // path_writable requirement — without it neither the static D6 check of
        // a constant path nor the runtime allowed-paths check would fire.
        [Test]
        public void CreateFolder_WritePortDeclaresPathWritable()
        {
            var requirements = new CreateFolderOperation().Descriptor.Requirements;

            Assert.That(
                requirements,
                Is.EquivalentTo(new[] { new Requirement("path_writable", "path") }),
                "the write port declares path_writable and nothing else (§G1 item 4)");
        }

        // The same requirement evaluated by the real §G1 evaluator with the
        // default allowedPaths: outside Assets/ — and a '..' — is OW-PERM-002,
        // never a silent success.
        [Test]
        public void PathWritableRequirement_RefusesOutsideAllowedPathsWithOwPerm002()
        {
            var evaluator = new EditorRequirementEvaluator(new[] { "Assets/" });
            var requirement = new Requirement("path_writable", "path");

            AssertSatisfied(evaluator.Evaluate(requirement, Json("\"Assets/Reports\"")));
            AssertViolated(
                evaluator.Evaluate(requirement, Json("\"External/Reports\"")),
                ErrorCodes.OW_PERM_002);
            AssertViolated(
                evaluator.Evaluate(requirement, Json("\"Assets/Reports/../../Outside\"")),
                ErrorCodes.OW_PERM_002);
        }

        // With Packages/ allowed by the settings, the same destination inside a
        // read-only package is OW-UNITY-010: the allowed check passes first, so
        // the read-only half is what refuses the write.
        [Test]
        public void PathWritableRequirement_AllowedCheckPrecedesTheReadOnlyCheck()
        {
            var evaluator = new EditorRequirementEvaluator(new[] { "Assets/", "Packages/" });
            var requirement = new Requirement("path_writable", "path");

            var result = evaluator.Evaluate(requirement, Json("\"" + ReadOnlyPackagePath() + "\""));

            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Violated));
            Assert.That(result.Diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_UNITY_010));
        }

        // ── the resolved-path write bound (SPEC §G7 item 1) ──────────────

        // F3 regression: the bound the Ref-targeted Operations apply to a
        // resolved asset path fails closed whenever the Run's frozen
        // allowedPaths snapshot is unavailable — a re-attached Run records
        // none, and a missing boundary is never "everything is allowed"
        // (§H4.1 decision D-R6). An empty set is just as closed.
        [Test]
        public void IsInsideAllowedPaths_UnavailableSnapshot_RefusesEverything()
        {
            Assert.That(
                AssetPathPolicy.IsInsideAllowedPaths("Assets/Reports/x.txt", null),
                Is.False,
                "a null snapshot must fail closed, not admit every path");
            Assert.That(
                AssetPathPolicy.IsInsideAllowedPaths(
                    "Assets/Reports/x.txt", Array.Empty<string>()),
                Is.False,
                "an empty snapshot admits nothing either");
        }

        // The resolved-path check is the declared path_writable rule
        // itself — the same helper the §G1 evaluator uses: a path counts
        // only when it begins with an allowed root plus '/', so the root
        // folder itself is outside and a sibling sharing a leading string
        // does not slip in.
        [Test]
        public void IsInsideAllowedPaths_RootFolderItself_IsOutside()
        {
            var allowed = new[] { "Assets/Allowed/" };
            Assert.That(
                AssetPathPolicy.IsInsideAllowedPaths("Assets/Allowed", allowed),
                Is.False,
                "the root folder itself is outside, matching the declared-port rule");
            Assert.That(
                AssetPathPolicy.IsInsideAllowedPaths("Assets/Allowed/x.txt", allowed),
                Is.True);
            Assert.That(
                AssetPathPolicy.IsInsideAllowedPaths("Assets/AllowedBroad/x.txt", allowed),
                Is.False,
                "a sibling prefix is not inside the allowed root");
            Assert.That(
                AssetPathPolicy.IsInsideAllowedPaths("assets/allowed/x.txt", allowed),
                Is.True,
                "containment stays case-insensitive like Unity asset paths");
        }

        private static void AssertSatisfied(RequirementResult result)
        {
            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Satisfied));
            Assert.That(result.Diagnostic, Is.Null, "a satisfied requirement carries no diagnostic");
        }

        private static void AssertViolated(RequirementResult result, string code)
        {
            Assert.That(result.Verdict, Is.EqualTo(RequirementVerdict.Violated), code);
            Assert.That(result.Diagnostic, Is.Not.Null);
            Assert.That(result.Diagnostic!.Code, Is.EqualTo(code));
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.That(parsed.Ok, Is.True, "fixture JSON must parse: " + text);
            return parsed.Value!;
        }
    }
}
