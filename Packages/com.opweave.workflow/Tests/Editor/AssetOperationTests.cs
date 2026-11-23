#nullable enable

// T-W2.7-01 [Edit] tests: local:T-W2.7-01:asset-path-simple-ops for the three
// §I.3 Asset Operations themselves — unity.asset.create_folder creating nested
// folders, succeeding on an existing one and refusing an escaped or read-only
// destination before any change; unity.asset.find returning at most 500
// references with the §G7 item 6 truncated marker, restricting the search to
// the normalized folders it is given and to the Run's readable roots when none
// are (§G7 item 1: the allowed paths plus the read-only Packages/);
// unity.asset.save_all writing a dirty asset to disk. Each Operation runs
// through a real UnityStepContext over the standard type registry, so the port
// conversions and the §F7 change record are the production ones.
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
    /// Per-Operation behaviour of <c>unity.asset.create_folder</c>,
    /// <c>unity.asset.find</c> and <c>unity.asset.save_all</c>.
    /// </summary>
    internal sealed class AssetOperationTests
    {
        private const string ProbeFolder = "Assets/OpWeaveAssetOps";

        // The collision fixture of the OW-UNITY-005 test: a plain file with no
        // extension, so the asset database holds a non-folder at exactly the
        // folder path the step asks for. An asset called "taken.asset" holds
        // the path "taken.asset", which is not the path a "taken" folder needs,
        // so only the extensionless file makes the refusal test a collision.
        private const string TakenPath = ProbeFolder + "/taken";

        // A package this project resolves from the registry — its contents are
        // readable but never writable (§G7 item 1).
        private const string ReadOnlyPackage = "Packages/com.unity.test-framework";

        // The §G7 item 1 read-bound fixtures: one Assets/ subfolder is the only
        // allowedPaths entry of these fixtures' Run, so the sibling folder is
        // genuinely outside what the search may cover.
        private const string AllowedFolder = "Assets/OpWeaveAssetOpsAllowed";
        private const string OutsideFolder = "Assets/OpWeaveAssetOpsOutside";
        private const string ScopeProbeName = "AssetOpsScopeProbe";

        // The default allowedPaths of the seam fixtures (§G7 item 1 "default
        // Assets/"), standing in for the snapshot a Run's acceptance freezes.
        private static readonly IReadOnlyList<string> ProjectAllowedPaths =
            new[] { "Assets/" };

        private static readonly IReadOnlyList<string> RestrictedAllowedPaths =
            new[] { AllowedFolder + "/" };

        private const string AssetPathType = "unity.AssetPath";

        private static readonly IReadOnlyDictionary<string, string> PathIn =
            new Dictionary<string, string> { ["path"] = AssetPathType };

        private static readonly IReadOnlyDictionary<string, string> PathOut =
            new Dictionary<string, string> { ["path"] = AssetPathType };

        private static readonly IReadOnlyDictionary<string, string> NoPorts =
            new Dictionary<string, string>();

        [SetUp]
        public void SetUp()
        {
            EnsureFolder(ProbeFolder);
        }

        [TearDown]
        public void TearDown()
        {
            // The collision fixture is a file this test wrote by hand rather
            // than an asset Unity created, so drop it through the database
            // before its folder goes.
            if (AssetDatabase.AssetPathToGUID(TakenPath).Length > 0)
            {
                AssetDatabase.DeleteAsset(TakenPath);
            }
            foreach (var folder in new[] { ProbeFolder, AllowedFolder, OutsideFolder })
            {
                if (AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.DeleteAsset(folder);
                }
            }
            AssetDatabase.Refresh();
        }

        // ── unity.asset.create_folder ──────────────────────────────────────

        [Test]
        public void CreateFolder_CreatesEveryMissingFolderAndOutputsTheNormalizedPath()
        {
            var changes = new StepChangeRecorder(0, "folder");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(
                        Json("\"Assets\\\\OpWeaveAssetOps//Inner/./Deep\"")),
                },
                PathIn, PathOut, changes);

            var result = new CreateFolderOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(OutputText(context, "path"), Is.EqualTo(ProbeFolder + "/Inner/Deep"));
            Assert.That(AssetDatabase.IsValidFolder(ProbeFolder + "/Inner"), Is.True);
            Assert.That(AssetDatabase.IsValidFolder(ProbeFolder + "/Inner/Deep"), Is.True);

            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Created));
            Assert.That(changes.Records[0].Target.Kind, Is.EqualTo("asset"));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(ProbeFolder + "/Inner/Deep"));
            // §I.3 declares Undo None, so the recorded change is not undoable.
            Assert.That(changes.Records[0].Undoable, Is.False);
        }

        [Test]
        public void CreateFolder_ExistingFolderSucceedsAndRecordsNoChange()
        {
            Assert.That(
                AssetDatabase.CreateFolder(ProbeFolder, "Already"),
                Is.Not.Empty,
                "the probe folder must be created");
            var changes = new StepChangeRecorder(0, "folder");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(Json("\"" + ProbeFolder + "/Already\"")),
                },
                PathIn, PathOut, changes);

            var result = new CreateFolderOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(OutputText(context, "path"), Is.EqualTo(ProbeFolder + "/Already"));
            Assert.That(changes.Records, Is.Empty,
                "the folder already existed, so nothing was created and nothing recorded");
        }

        [Test]
        public void CreateFolder_RefusesAnEscapingPathWithOwPerm002AndCreatesNothing()
        {
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(
                        Json("\"" + ProbeFolder + "/../../OpWeaveAssetOpsEscape\"")),
                },
                PathIn, PathOut);

            var result = new CreateFolderOperation().Execute(context);

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "path");
            Assert.That(
                AssetDatabase.IsValidFolder("Assets/OpWeaveAssetOpsEscape"),
                Is.False,
                "a refused path must not leave a folder behind");
        }

        [Test]
        public void CreateFolder_RefusesAReadOnlyPackageDestinationWithOwUnity010()
        {
            var destination = ReadOnlyPackage + "/Editor/OpWeaveAssetOpsProbe";
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(Json("\"" + destination + "\"")),
                },
                PathIn, PathOut);

            var result = new CreateFolderOperation().Execute(context);

            AssertFailedWith(result, ErrorCodes.OW_UNITY_010, "path");
            Assert.That(
                Directory.Exists(PhysicalPath(destination)),
                Is.False,
                "a refused destination must not leave a folder behind");
        }

        // §G7's error row: OW-UNITY-005 is the invalid path or the collision
        // with an existing asset the Editor reports when it cannot create. The
        // collision has to sit at exactly the requested folder path — see
        // TakenPath for why a "taken.asset" asset holds a different path and
        // would leave the folder free to be created.
        [Test]
        public void CreateFolder_NameTakenByAnExistingAssetFailsWithOwUnity005()
        {
            CreatePlainFile(TakenPath);
            // The name must really be taken by a non-folder — on disk and in
            // the database, at exactly the requested path — or the step under
            // test would be free to create its folder and the refusal below
            // would prove nothing.
            Assert.That(File.Exists(PhysicalPath(TakenPath)), Is.True,
                "the collision file must be written before the folder is refused");
            Assert.That(AssetDatabase.IsValidFolder(TakenPath), Is.False,
                "the taken path is a plain file, not a folder");
            Assert.That(AssetDatabase.AssetPathToGUID(TakenPath), Is.Not.Empty,
                "the asset database must hold the collision file at the requested path");

            var changes = new StepChangeRecorder(0, "folder");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(Json("\"" + TakenPath + "\"")),
                },
                PathIn, PathOut, changes);

            var result = new CreateFolderOperation().Execute(context);

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "path");
            Assert.That(
                AssetDatabase.IsValidFolder(TakenPath),
                Is.False,
                "a refused path must not leave a folder behind");
            Assert.That(changes.Records, Is.Empty,
                "a refused create records no change");
            Assert.That(File.Exists(PhysicalPath(TakenPath)), Is.True,
                "a refused create must leave the file that held the name alone");
        }

        // A Dry Run must hear about a refused path from Preflight, which applies
        // no change (SPEC §E4 item 3, §D8).
        [Test]
        public void CreateFolderPreflight_ReportsTheRefusedPathWithoutChangingAnything()
        {
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["path"] = InputValue.Present(
                        Json("\"Assets/../OpWeaveAssetOpsEscape\"")),
                },
                PathIn, PathOut);

            var preflight = new CreateFolderOperation().Preflight(context);

            Assert.That(preflight.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(preflight.Diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_PERM_002));
            Assert.That(preflight.PlannedChanges, Is.Empty);
            Assert.That(
                AssetDatabase.IsValidFolder("Assets/OpWeaveAssetOpsEscape"),
                Is.False);
        }

        // The §D8 item 1 pending rule: an edge-fed port has no value to
        // inspect, so Preflight reports nothing instead of failing the Dry Run.
        [Test]
        public void CreateFolderPreflight_SkipsAPendingPath()
        {
            var context = Context(
                new Dictionary<string, InputValue> { ["path"] = InputValue.Missing },
                PathIn, PathOut,
                pending: new Dictionary<string, string> { ["path"] = "producer" });

            var preflight = new CreateFolderOperation().Preflight(context);

            Assert.That(preflight.Diagnostics, Is.Empty);
        }

        // ── unity.asset.find ───────────────────────────────────────────────

        [Test]
        public void Find_ReturnsAtMost500ReferencesAndMarksTruncated()
        {
            var guids = Enumerable.Range(0, 501).Select(i => "guid-" + i).ToArray();
            var context = FindContext();

            var result = FindOperation(guids).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var assets = (JArray)context.Outputs["assets"].ToToken();
            Assert.That(assets.Count, Is.EqualTo(FindAssetsOperation.MaxResults));
            Assert.That(OutputBool(context, "truncated"), Is.True,
                "more than the §G7 item 6 cap matched, so truncated must be set");
            var first = (JObject)assets[0]!;
            Assert.That((string?)first["kind"], Is.EqualTo("asset"));
            Assert.That((string?)first["guid"], Is.EqualTo("guid-0"));
            Assert.That((string?)first["path"], Is.EqualTo("Assets/Found/guid-0.mat"));
            AssertOutputsMatchDeclaredPortTypes(context);
        }

        [Test]
        public void Find_ResultsAtTheCapAreNotTruncated()
        {
            var guids = Enumerable.Range(0, FindAssetsOperation.MaxResults)
                .Select(i => "guid-" + i).ToArray();
            var context = FindContext();

            var result = FindOperation(guids).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var assets = (JArray)context.Outputs["assets"].ToToken();
            Assert.That(assets.Count, Is.EqualTo(FindAssetsOperation.MaxResults));
            Assert.That(OutputBool(context, "truncated"), Is.False);
        }

        [Test]
        public void Find_SearchesTheNormalizedFoldersItIsGiven()
        {
            string[]? searchedFolders = null;
            string? searchedFilter = null;
            var context = FindContext(new Dictionary<string, InputValue>
            {
                ["filter"] = InputValue.Present(Json("\"t:Material\"")),
                ["folders"] = InputValue.Present(
                    Json("[\"Assets//Materials/\", \"Assets\\\\Shaders\"]")),
            });

            var result = new FindAssetsOperation(
                (filter, folders) =>
                {
                    searchedFilter = filter;
                    searchedFolders = folders;
                    return Array.Empty<string>();
                },
                guid => "Assets/x",
                () => ProjectAllowedPaths).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(searchedFilter, Is.EqualTo("t:Material"));
            Assert.That(searchedFolders, Is.EqualTo(new[] { "Assets/Materials", "Assets/Shaders" }),
                "every folder reaches FindAssets in its normalized form");
        }

        // §G7 item 1: an omitted folders port narrows the search to what the Run
        // may read — its allowed paths plus the read-only Packages root — instead
        // of handing the Editor the whole project.
        [Test]
        public void Find_WithoutFoldersSearchesItsReadableRoots()
        {
            string[]? searchedFolders = new[] { "not called" };
            var context = FindContext();

            var result = new FindAssetsOperation(
                (filter, folders) =>
                {
                    searchedFolders = folders;
                    return Array.Empty<string>();
                },
                guid => "Assets/x",
                () => ProjectAllowedPaths).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(searchedFolders, Is.EqualTo(new[] { "Assets", "Packages" }),
                "the roots are searched in the folder spelling FindAssets takes, never " +
                "the empty array that would search the whole project");
        }

        // With no Run snapshot there is nothing to allow but the read-only root,
        // the same fail-closed reading the path_writable evaluator applies to an
        // empty allowed set.
        [Test]
        public void Find_WithoutASettingsSnapshotReadsThePackagesRootOnly()
        {
            string[]? searchedFolders = new[] { "not called" };
            var context = FindContext();

            var omitted = new FindAssetsOperation(
                (filter, folders) =>
                {
                    searchedFolders = folders;
                    return Array.Empty<string>();
                },
                guid => "Packages/x",
                () => null).Execute(context);

            Assert.That(omitted.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(omitted));
            Assert.That(searchedFolders, Is.EqualTo(new[] { "Packages" }),
                "no snapshot leaves Packages/ as the only readable root");

            var explicitFolder = FindContext(new Dictionary<string, InputValue>
            {
                ["filter"] = InputValue.Present(Json("\"t:Material\"")),
                ["folders"] = InputValue.Present(Json("[\"Assets/Materials\"]")),
            });

            AssertFailedWith(
                new FindAssetsOperation(
                    (filter, folders) => Array.Empty<string>(),
                    guid => "Assets/x",
                    () => null).Execute(explicitFolder),
                ErrorCodes.OW_PERM_002,
                "folders");
        }

        [Test]
        public void Find_RefusesAnEscapingFolderWithOwPerm002WithoutSearching()
        {
            var searched = false;
            var context = FindContext(new Dictionary<string, InputValue>
            {
                ["filter"] = InputValue.Present(Json("\"t:Material\"")),
                ["folders"] = InputValue.Present(Json("[\"Assets/../Secrets\"]")),
            });

            var result = new FindAssetsOperation(
                (filter, folders) =>
                {
                    searched = true;
                    return Array.Empty<string>();
                },
                guid => "Assets/x",
                () => ProjectAllowedPaths).Execute(context);

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "folders");
            Assert.That(searched, Is.False, "no search runs once a folder is refused");
        }

        // §G7 item 1: a folder outside the Run's allowed paths is OW-PERM-002
        // before the Editor is asked to search, and the refused text is never
        // echoed back.
        [Test]
        public void Find_RefusesAFolderOutsideTheAllowedPathsWithoutSearching()
        {
            var searched = false;
            var context = FindContext(new Dictionary<string, InputValue>
            {
                ["filter"] = InputValue.Present(Json("\"t:Material\"")),
                ["folders"] = InputValue.Present(Json("[\"" + OutsideFolder + "\"]")),
            });

            var result = new FindAssetsOperation(
                (filter, folders) =>
                {
                    searched = true;
                    return Array.Empty<string>();
                },
                guid => "Assets/x",
                () => RestrictedAllowedPaths).Execute(context);

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "folders");
            Assert.That(searched, Is.False,
                "a folder the Run may not read must be refused before any search");
            Assert.That(result.Error!.Message, Does.Not.Contain(OutsideFolder),
                "the refused path is never echoed by the permission refusal");
        }

        // The same clause on the search's output: assets that exist outside the
        // allowed paths must not be reported, while the allowed ones are.
        [Test]
        public void Find_ReportsOnlyAssetsInsideTheAllowedFolderItIsGiven()
        {
            CreateScopeProbes();
            var context = FindContext(new Dictionary<string, InputValue>
            {
                ["filter"] = InputValue.Present(Json("\"" + ScopeProbeName + "\"")),
                ["folders"] = InputValue.Present(Json("[\"" + AllowedFolder + "\"]")),
            });

            var result = LiveFind(RestrictedAllowedPaths).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var paths = FoundPaths(context);
            Assert.That(paths, Contains.Item(AllowedFolder + "/" + ScopeProbeName + ".asset"));
            Assert.That(
                paths.Any(p => p!.StartsWith(OutsideFolder + "/", StringComparison.Ordinal)),
                Is.False,
                "an asset outside the allowed paths must never be reported");
            AssertOutputsMatchDeclaredPortTypes(context);

            // The control: the same probe *is* reported once its own folder is
            // allowed, so the exclusion above is the read-bound and not a
            // fixture the search could never have found.
            var outside = FindContext(new Dictionary<string, InputValue>
            {
                ["filter"] = InputValue.Present(Json("\"" + ScopeProbeName + "\"")),
                ["folders"] = InputValue.Present(Json("[\"" + OutsideFolder + "\"]")),
            });
            var outsideResult = LiveFind(new[] { OutsideFolder + "/" }).Execute(outside);

            Assert.That(outsideResult.Kind, Is.EqualTo(StepResultKind.Succeeded),
                () => Describe(outsideResult));
            Assert.That(FoundPaths(outside),
                Contains.Item(OutsideFolder + "/" + ScopeProbeName + ".asset"),
                () => "the outside probe must be findable at all: " +
                    string.Join(", ", FoundPaths(outside)));
        }

        // An omitted folders port must not widen the search past those roots:
        // the probe outside the allowed root stays unreported.
        [Test]
        public void Find_WithoutFoldersOmitsAssetsOutsideTheAllowedRoot()
        {
            CreateScopeProbes();
            var context = FindContext(new Dictionary<string, InputValue>
            {
                ["filter"] = InputValue.Present(Json("\"" + ScopeProbeName + "\"")),
            });

            var result = LiveFind(RestrictedAllowedPaths).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var paths = FoundPaths(context);
            Assert.That(paths, Contains.Item(AllowedFolder + "/" + ScopeProbeName + ".asset"));
            Assert.That(
                paths.Any(p => p!.StartsWith(OutsideFolder + "/", StringComparison.Ordinal)),
                Is.False,
                "no folders means the readable roots, not the whole project");
            AssertOutputsMatchDeclaredPortTypes(context);
        }

        // §G7 item 1 keeps Packages/ readable even when the Run's allowedPaths
        // name an Assets/ subfolder only: the folder is searched and its hits
        // survive the output bound.
        [Test]
        public void Find_StillSearchesAPackageFolderOutsideTheAllowedPaths()
        {
            const string packageFolder = "Packages/com.opweave.workflow/Editor/Operations";
            string[]? searchedFolders = null;
            var context = FindContext(new Dictionary<string, InputValue>
            {
                ["filter"] = InputValue.Present(Json("\"FindAssetsOperation\"")),
                ["folders"] = InputValue.Present(Json("[\"" + packageFolder + "\"]")),
            });

            var result = new FindAssetsOperation(
                (filter, folders) =>
                {
                    searchedFolders = folders;
                    return AssetDatabase.FindAssets(filter, folders);
                },
                AssetDatabase.GUIDToAssetPath,
                () => RestrictedAllowedPaths).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(searchedFolders, Is.EqualTo(new[] { packageFolder }),
                "a folder inside the read-only Packages root is allowed even though it " +
                "lies outside the Run's allowed paths");
            Assert.That(
                FoundPaths(context),
                Contains.Item(packageFolder + "/FindAssetsOperation.cs"),
                () => "the package's own asset must be reported: " +
                    string.Join(", ", FoundPaths(context)));
            AssertOutputsMatchDeclaredPortTypes(context);
        }

        // The live binding: assets really created in the project come back as
        // §B5 AssetRef values that satisfy the declared port types.
        [Test]
        public void Find_LiveSearchReturnsTheCreatedAssetsAsAssetRefs()
        {
            var firstPath = ProbeFolder + "/AssetOpsFindProbe.asset";
            var secondPath = ProbeFolder + "/AssetOpsFindProbe2.asset";
            CreateProbeAsset(firstPath);
            CreateProbeAsset(secondPath);
            var context = FindContext(new Dictionary<string, InputValue>
            {
                ["filter"] = InputValue.Present(Json("\"AssetOpsFindProbe\"")),
                ["folders"] = InputValue.Present(Json("[\"" + ProbeFolder + "\"]")),
            });

            var result = LiveFind(ProjectAllowedPaths).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            var assets = (JArray)context.Outputs["assets"].ToToken();
            var hits = assets.OfType<JObject>().ToList();
            var paths = hits.Select(obj => (string?)obj["path"]).ToList();
            Assert.That(paths, Contains.Item(firstPath));
            Assert.That(paths, Contains.Item(secondPath));
            Assert.That(
                hits.All(obj => (string?)obj["kind"] == "asset" &&
                    (string?)obj["guid"] == AssetDatabase.AssetPathToGUID((string?)obj["path"]!)),
                Is.True,
                "every hit carries the §B5 asset kind and the guid of its own path");
            Assert.That(OutputBool(context, "truncated"), Is.False);

            AssertOutputsMatchDeclaredPortTypes(context);
        }

        // ── unity.asset.save_all ───────────────────────────────────────────

        [Test]
        public void SaveAll_WritesTheDirtyAssetToDisk()
        {
            var path = ProbeFolder + "/AssetOpsDirty.asset";
            var asset = CreateProbeAsset(path);
            Assert.That(ReadSerializedMarker(path), Is.EqualTo(0),
                "the freshly created asset is on disk at its default value");

            asset.marker = 7;
            EditorUtility.SetDirty(asset);
            Assert.That(EditorUtility.IsDirty(asset), Is.True,
                "the fixture must leave the asset dirty before the step runs");
            Assert.That(ReadSerializedMarker(path), Is.EqualTo(0),
                "a dirty change is not on disk yet");

            var result = new SaveAllOperation().Execute(
                Context(new Dictionary<string, InputValue>(), NoPorts, NoPorts));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(ReadSerializedMarker(path), Is.EqualTo(7),
                "the dirty change must reach the file");
            Assert.That(EditorUtility.IsDirty(asset), Is.False,
                "after the save nothing is pending for that asset");
        }

        [Test]
        public void SaveAll_SucceedsWhenNothingIsDirty()
        {
            var result = new SaveAllOperation().Execute(
                Context(new Dictionary<string, InputValue>(), NoPorts, NoPorts));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
        }

        // ── fixtures ───────────────────────────────────────────────────────

        // Creates every missing segment of an Assets/ folder path through the
        // asset database, outermost first, so an asset created under it has a
        // real directory on disk and the database holds both.
        private static void EnsureFolder(string folderPath)
        {
            var segments = folderPath.Split('/');
            var parent = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var child = parent + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(child))
                {
                    Assert.That(
                        AssetDatabase.CreateFolder(parent, segments[i]),
                        Is.Not.Empty,
                        "the probe folder must be created: " + child);
                }
                parent = child;
            }
        }

        private static string ParentFolder(string path) =>
            path.Substring(0, path.LastIndexOf('/'));

        // The pair of same-named probe assets the §G7 item 1 read-bound
        // fixtures search for: one inside the restricted allowedPaths entry, one
        // in its sibling folder outside it. A single name matched by one filter
        // is what turns "the other one is not reported" into evidence.
        private static void CreateScopeProbes()
        {
            CreateProbeAsset(AllowedFolder + "/" + ScopeProbeName + ".asset");
            CreateProbeAsset(OutsideFolder + "/" + ScopeProbeName + ".asset");
        }

        // A unity.asset.find step over the real asset database, with only the
        // Run's read roots supplied by the test.
        private static FindAssetsOperation LiveFind(IReadOnlyList<string> allowedPaths) =>
            new FindAssetsOperation(
                AssetDatabase.FindAssets,
                AssetDatabase.GUIDToAssetPath,
                () => allowedPaths);

        // The paths of the assets a find step reported.
        private static List<string?> FoundPaths(UnityStepContext context) =>
            ((JArray)context.Outputs["assets"].ToToken())
            .OfType<JObject>()
            .Select(obj => (string?)obj["path"])
            .ToList();

        private static ProbeAsset CreateProbeAsset(string path)
        {
            EnsureFolder(ParentFolder(path));
            var created = ScriptableObject.CreateInstance<ProbeAsset>();
            AssetDatabase.CreateAsset(created, path);
            var asset = AssetDatabase.LoadAssetAtPath<ProbeAsset>(path);
            Assert.That(asset, Is.Not.Null, "the probe asset must exist: " + path);
            return asset!;
        }

        // Writes a plain file with no extension and imports it synchronously,
        // the way PrefabContentFingerprintTests' WriteImportedAsset does. Having
        // no known asset suffix, the Editor holds it as a DefaultAsset at
        // exactly this path, which is the state that occupies a folder name.
        private static void CreatePlainFile(string assetPath)
        {
            EnsureFolder(ParentFolder(assetPath));
            File.WriteAllText(PhysicalPath(assetPath), string.Empty);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string> inputTypes,
            IReadOnlyDictionary<string, string> outputTypes,
            IChangeRecorder? changes = null,
            IReadOnlyDictionary<string, string>? pending = null) =>
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

        // The context of a unity.asset.find step: filter (and optionally
        // folders) in, assets and truncated out.
        private static UnityStepContext FindContext(
            IReadOnlyDictionary<string, InputValue>? inputs = null) =>
            Context(
                inputs ?? new Dictionary<string, InputValue>
                {
                    ["filter"] = InputValue.Present(Json("\"t:Material\"")),
                },
                new Dictionary<string, string>
                {
                    ["filter"] = "core.String",
                    ["folders"] = "core.List<unity.AssetPath>",
                },
                new Dictionary<string, string>
                {
                    ["assets"] = "core.List<unity.AssetRef<unity.Object>>",
                    ["truncated"] = "core.Bool",
                });

        // A find step over the seam: the given guids are the search result and
        // each maps to a predictable path.
        private static FindAssetsOperation FindOperation(string[] guids) =>
            new FindAssetsOperation(
                (filter, folders) => guids,
                guid => "Assets/Found/" + guid + ".mat",
                () => ProjectAllowedPaths);

        private static string OutputText(UnityStepContext context, string port) =>
            (string?)context.Outputs[port].ToToken() ?? string.Empty;

        private static bool OutputBool(UnityStepContext context, string port) =>
            (bool)context.Outputs[port].ToToken();

        private static void AssertFailedWith(StepResult result, string code, string port)
        {
            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed), () => Describe(result));
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo(code), () => Describe(result));
            Assert.That(result.Error!.Port, Is.EqualTo(port));
        }

        private static string Describe(StepResult result) =>
            result.Kind +
            (result.Error is null
                ? string.Empty
                : " " + result.Error.Code + " " + result.Error.Message);

        // The physical location of a project asset path. An Assets/ path sits
        // under the Assets folder the Editor itself names — the project root's
        // *sibling* of that name is not the same directory. A Packages/ path
        // resolves through its owning package (a local package may live outside
        // the project root, a registry package sits in the read-only cache).
        private static string PhysicalPath(string assetPath)
        {
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return Path.Combine(
                    Application.dataPath,
                    assetPath.Substring("Assets/".Length)
                        .Replace('/', Path.DirectorySeparatorChar));
            }
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var info = UnityEditor.PackageManager.PackageInfo
                    .FindForAssetPath(assetPath);
                Assert.That(info, Is.Not.Null,
                    "the fixture path must belong to a resolved package: " + assetPath);
                var rest = assetPath.Substring(info!.assetPath.Length).TrimStart('/');
                return rest.Length == 0
                    ? info.resolvedPath
                    : Path.Combine(info.resolvedPath,
                        rest.Replace('/', Path.DirectorySeparatorChar));
            }
            Assert.Fail("not a project asset path: " + assetPath);
            return string.Empty;
        }

        private static int ReadSerializedMarker(string path)
        {
            var file = PhysicalPath(path);
            Assert.That(File.Exists(file), Is.True,
                "the asset must be written on disk at its own path: " + file);
            var text = File.ReadAllText(file);
            var start = text.IndexOf("marker: ", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0),
                "the serialized asset must carry the marker field: " + path);
            var digits = new string(text.Substring(start + "marker: ".Length)
                .TakeWhile(char.IsDigit).ToArray());
            Assert.That(digits, Is.Not.Empty, "the marker value must be an integer: " + path);
            return int.Parse(digits);
        }

        // The §E5/§E4 step-7 contract the Runner applies to the outputs, run
        // here directly: every emitted value satisfies its declared port type.
        private static void AssertOutputsMatchDeclaredPortTypes(UnityStepContext context)
        {
            var declared = new Dictionary<string, string>
            {
                ["assets"] = "core.List<unity.AssetRef<unity.Object>>",
                ["truncated"] = "core.Bool",
            };
            foreach (var pair in declared)
            {
                var parsed = TypeRef.Parse(pair.Value);
                Assert.That(parsed.Ok, Is.True, pair.Value);
                var diagnostics = ValueValidation.Validate(
                    RunnerHostFixture.Types, parsed.Value!, context.Outputs[pair.Key], null, false);
                Assert.That(diagnostics, Is.Empty,
                    () => pair.Key + ": " + string.Join(
                        "; ", diagnostics.Select(d => d.Code + " " + d.Message + " " + d.JsonPath)));
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
