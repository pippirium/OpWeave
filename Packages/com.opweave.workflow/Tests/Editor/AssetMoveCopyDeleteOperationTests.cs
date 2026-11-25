#nullable enable

// T-W2.7-04 [Edit] tests: local:T-W2.7-04:asset-move-copy-delete and
// local:T-W2.7-04:asset-mcd-reconcile for unity.asset.move,
// unity.asset.copy and unity.asset.delete (SPEC §I.3, §G7 item 5, §F5).
//
// Coverage:
//  - move: ValidateMoveAsset before MoveAsset; rename in one folder and a
//    move into a subfolder keep the guid; existing or folder-less
//    destination, an escaped path, a read-only package destination or a
//    read-only package source refuse before any mutation.
//  - copy: CopyAsset produces a fresh guid with the same content; a
//    read-only package is a legal copy source; destination refusals match
//    move's.
//  - delete: trash by default, permanent only on mode=permanent; a
//    read-only package target and a bad mode refuse — OW-OP-006 for the
//    enum violation, OW-UNITY-010 for the package.
//  - the §G7 item 1 frozen boundary on resolved write paths: a target
//    outside the Run's accepted allowedPaths is OW-PERM-002 before any
//    mutation, an unavailable snapshot fails closed the same way (a
//    re-attached Run records none), and the allowed root folder itself
//    is outside, matching the declared path_writable containment.
//  - The six §F5 Operation-level cases at Reconcile for each Operation:
//    not executed; executed but StepSucceeded not recorded; partial side
//    effect; pre-existing same-name artifact; artifact modified by another
//    operation; simulated Editor restart (different editorSessionId — the
//    file artifact stays re-identifiable by its permanent path/guid/
//    content keys).
//  - One [UnityTest] per Operation crosses a real domain reload (SPEC
//    §T.3): Reconcile on the persisted Preflight snapshot returns
//    Completed with the uninterrupted outputs.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Recovery;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Behaviour and §F5 reconciliation of <c>unity.asset.move</c>,
    /// <c>unity.asset.copy</c> and <c>unity.asset.delete</c>.
    /// </summary>
    internal sealed class AssetMoveCopyDeleteOperationTests
    {
        private const string ProbeFolder = "Assets/OpWeaveAssetMCD";
        private const string MarkerFolder = "Assets/Editor/OpWeaveAssetMCDProbe";
        private const string MarkerSource =
            "#nullable enable\n" +
            "namespace OpWeaveAssetMCDProbe\n{\n" +
            "    internal static class ReloadMarker { public const int V = 1; }\n" +
            "}\n";

        // A registry-package asset — read-only, so it is the source or
        // destination no write may touch; embedded packages are writable
        // and would not prove the check (SPEC §G7 item 1).
        private const string PackageFolder = "Packages/com.unity.test-framework";
        private const string PackageAsset = PackageFolder + "/package.json";

        // The §G7 item 1 allowed-paths fixtures: one narrowed allowedPaths
        // entry injected through the Operations' seam, so the probe folder
        // is genuinely outside what the Run may write (the
        // AssetOperationTests read-bound pattern).
        private const string AllowedFolder = "Assets/OpWeaveAssetMCDAllowed";

        private static readonly IReadOnlyList<string> RestrictedAllowedPaths =
            new[] { AllowedFolder + "/" };

        // The unbounded-Run fixture: the accepted boundary that admits every
        // path a default-construction test exercises — the settings'
        // documented Assets/ default plus Packages/, whose read-only-package
        // half the write-path checks still refuse after the boundary admits.
        // A missing snapshot means "nothing is allowed", never "everything"
        // (SPEC §G7 item 1, §H4.1 decision D-R6), so tests must inject the
        // boundary they mean explicitly.
        private static readonly IReadOnlyList<string> PermissiveAllowedPaths =
            new[] { "Assets/", "Packages/" };

        private static readonly IReadOnlyDictionary<string, string> MoveInputs =
            new Dictionary<string, string>
            {
                ["asset"] = "unity.AssetRef<unity.Object>",
                ["destination"] = "unity.AssetPath",
            };

        private static readonly IReadOnlyDictionary<string, string> MoveOutputs =
            new Dictionary<string, string>
            {
                ["asset"] = "unity.AssetRef<unity.Object>",
            };

        private static readonly IReadOnlyDictionary<string, string> DeleteInputs =
            new Dictionary<string, string>
            {
                ["asset"] = "unity.AssetRef<unity.Object>",
                ["mode"] = "core.String",
            };

        private static readonly IReadOnlyDictionary<string, string> NoOutputs =
            new Dictionary<string, string>();

        [SetUp]
        public void SetUp() => EnsureFolder(ProbeFolder);

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            if (AssetDatabase.IsValidFolder(AllowedFolder))
            {
                AssetDatabase.DeleteAsset(AllowedFolder);
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
        // earlier run — remove it so the next test starts from a clean
        // project (the AssetCreateOperationTests pattern). Setup and
        // teardown must never yield an ExpectDomainReload instruction
        // (RecompileScripts): UTF's PrepareForDomainReload records the
        // CURRENT TestEnumerator's program counter — still a different
        // test's method enumerator during setup and teardown — and the
        // resume then applies that stale PC to the next test method's
        // fresh enumerator, jumping it past phase-1 entirely when the
        // captured enumerator was abandoned mid-state. The marker deletion
        // queues its compile under the runner's LockReloadAssemblies, so
        // the reload it needs drains at the next test body's own
        // `yield return new RecompileScripts()`, whose captured PC is the
        // correct one.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            Crumb("setup cleanup enter; stateDir=" + Directory.Exists(ProbeStateDir));
            CleanupMarker();
            yield break;
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            Crumb("teardown cleanup enter; stateDir=" + Directory.Exists(ProbeStateDir));
            CleanupMarker();
            yield break;
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
            Path.Combine(ProjectRoot(), "Library", "OpWeaveAssetMCDProbe");

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));

        // ══ unity.asset.move — Execute ═════════════════════════════════

        [Test]
        public void Move_Execute_MovesAsset_KeepingGuidAndContent()
        {
            var source = ProbeFolder + "/Moving.txt";
            var sub = ProbeFolder + "/Sub";
            EnsureFolder(sub);
            var destination = sub + "/Moved.txt";
            var guid = CreateTextAsset(source, "move me\n");
            var changes = new StepChangeRecorder(0, "mv");
            var context = MoveContext(source, destination, changes);

            var result = new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(File.Exists(PhysicalPath(source)), Is.False, "the source is gone");
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    destination, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.EqualTo(guid), "the move keeps the guid");
            Assert.That(
                File.ReadAllText(PhysicalPath(destination)), Is.EqualTo("move me\n"));

            var output = (JObject)context.Outputs["asset"].ToToken();
            Assert.That((string?)output["kind"], Is.EqualTo("asset"));
            Assert.That((string?)output["path"], Is.EqualTo(destination));
            Assert.That((string?)output["guid"], Is.EqualTo(guid));
            AssertOutputsMatchDeclaredPortTypes(context, MoveOutputs);

            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Moved));
            Assert.That(changes.Records[0].Target.Kind, Is.EqualTo("asset"));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(destination));
            Assert.That(changes.Records[0].Target.Guid, Is.EqualTo(guid));
            Assert.That(changes.Records[0].Undoable, Is.False);
        }

        // A rename is a move to a new name in the same folder (SPEC §G7
        // item 5's rename note).
        [Test]
        public void Move_Execute_RenamesInSameFolder()
        {
            var source = ProbeFolder + "/OldName.txt";
            var destination = ProbeFolder + "/NewName.txt";
            var guid = CreateTextAsset(source, "rename me\n");

            var result = new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(MoveContext(source, destination));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    destination, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.EqualTo(guid));
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    source, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.Empty);
        }

        [Test]
        public void Move_Execute_ExistingDestination_Fails_OwUnity005_AndLeavesBoth()
        {
            var source = ProbeFolder + "/Source.txt";
            var destination = ProbeFolder + "/Taken.txt";
            var sourceGuid = CreateTextAsset(source, "source\n");
            CreateTextAsset(destination, "kept\n");

            var result = new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(MoveContext(source, destination));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "destination");
            Assert.That(File.ReadAllText(PhysicalPath(destination)), Is.EqualTo("kept\n"),
                "an existing artifact is never overwritten");
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    source, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.EqualTo(sourceGuid), "a refused move leaves the source untouched");

            var preflight = new MoveAssetOperation(() => PermissiveAllowedPaths).Preflight(
                MoveContext(source, destination));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_005),
                "Preflight reports the occupied destination too (Dry Run truth)");
        }

        [Test]
        public void Move_Execute_MissingDestinationFolder_Fails_OwUnity005()
        {
            var source = ProbeFolder + "/Orphan.txt";
            CreateTextAsset(source, "orphan\n");

            var result = new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(
                MoveContext(source, "Assets/OpWeaveAssetMCDNoSuchDir/Orphan.txt"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "destination");
            Assert.That(File.Exists(PhysicalPath(source)), Is.True,
                "a refused move leaves the source in place");
        }

        [Test]
        public void Move_Execute_EscapedDestination_Fails_OwPerm002()
        {
            var source = ProbeFolder + "/Escape.txt";
            CreateTextAsset(source, "escape\n");

            var result = new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(
                MoveContext(source, "Assets/../OpWeaveAssetMCDEscape/Escaped.txt"));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "destination");
            Assert.That(File.Exists(PhysicalPath(source)), Is.True);
        }

        // A destination inside a read-only package is refused before the
        // Editor sees it (SPEC §G7 item 1).
        [Test]
        public void Move_Execute_PackageDestination_Fails_OwUnity010()
        {
            var source = ProbeFolder + "/ToPackage.txt";
            CreateTextAsset(source, "to package\n");

            var result = new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(
                MoveContext(source, PackageFolder + "/Injected.txt"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_010, "destination");
            Assert.That(File.Exists(PhysicalPath(source)), Is.True);
        }

        // Moving an asset out of a read-only package is a write inside
        // that package — refused before anything moves (SPEC §G7 item 1:
        // Packages/ is readable only).
        [Test]
        public void Move_Execute_PackageSource_Fails_OwUnity010()
        {
            var result = new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(
                MoveContext(PackageAsset, ProbeFolder + "/Stolen.json"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_010, "asset");
            Assert.That(
                AssetDatabase.AssetPathToGUID(PackageAsset), Is.Not.Empty,
                "the read-only package asset must not have moved");
            Assert.That(
                File.Exists(PhysicalPath(ProbeFolder + "/Stolen.json")), Is.False);
        }

        // §G7 item 1 regression: the resolved source of a move is a write
        // at its own path, so it is judged by the Run's allowedPaths like
        // the declared destination — an AssetRef outside a narrowed
        // allowedPaths setting is OW-PERM-002 before anything moves, even
        // when the destination is inside the set.
        [Test]
        public void Move_Execute_SourceOutsideAllowedPaths_Fails_OwPerm002()
        {
            var source = ProbeFolder + "/OutsideSource.txt";
            var destination = AllowedFolder + "/MovedIn.txt";
            EnsureFolder(AllowedFolder);
            CreateTextAsset(source, "outside\n");

            var result = new MoveAssetOperation(() => RestrictedAllowedPaths)
                .Execute(MoveContext(source, destination));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "asset");
            Assert.That(File.Exists(PhysicalPath(source)), Is.True,
                "a refused move leaves the source in place");
            Assert.That(File.Exists(PhysicalPath(destination)), Is.False,
                "nothing arrives at the destination either");
            Assert.That(result.Error!.Message, Does.Not.Contain(source),
                "the refused path is never echoed by the permission refusal");

            var preflight = new MoveAssetOperation(() => RestrictedAllowedPaths)
                .Preflight(MoveContext(source, destination));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_PERM_002),
                "Preflight reports the outside source too (Dry Run truth)");
        }

        // The same narrowed bound admits the move when both ends are
        // inside it — the refusal above is the boundary, not the fixture.
        [Test]
        public void Move_Execute_SourceInsideAllowedPaths_StillMoves()
        {
            var source = AllowedFolder + "/InsideSource.txt";
            var destination = AllowedFolder + "/MovedInside.txt";
            EnsureFolder(AllowedFolder);
            var guid = CreateTextAsset(source, "inside\n");

            var result = new MoveAssetOperation(() => RestrictedAllowedPaths)
                .Execute(MoveContext(source, destination));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                () => Describe(result));
            Assert.That(File.Exists(PhysicalPath(source)), Is.False);
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    destination, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.EqualTo(guid), "the move keeps the guid");
        }

        // The F3 fail-closed half (SPEC §G7 item 1, §H4.1 decision D-R6):
        // a missing snapshot is never "everything is allowed" — the frozen
        // boundary a Run was accepted under cannot be produced, so the
        // resolved source is OW-PERM-002 before anything moves. This is
        // the state a re-attached Run is in after a domain reload or
        // Editor restart, when the accepted-settings entry lives only in
        // the previous domain's memory.
        [Test]
        public void Move_Execute_NullSnapshot_Fails_OwPerm002_AndMovesNothing()
        {
            var source = ProbeFolder + "/NoSnapshot.txt";
            var destination = ProbeFolder + "/NoSnapshotMoved.txt";
            CreateTextAsset(source, "no snapshot\n");

            var result = new MoveAssetOperation(() => null)
                .Execute(MoveContext(source, destination));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "asset");
            Assert.That(File.Exists(PhysicalPath(source)), Is.True,
                "a refused move leaves the source in place");
            Assert.That(File.Exists(PhysicalPath(destination)), Is.False,
                "nothing arrives at the destination either");

            var preflight = new MoveAssetOperation(() => null)
                .Preflight(MoveContext(source, destination));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_PERM_002),
                "Preflight reports the missing boundary too (Dry Run truth)");
        }

        // The declared path_writable rule counts a path inside an allowed
        // set only when it begins with an allowed root plus '/': the root
        // folder itself is outside, so moving the allowed root away is
        // refused — the resolved-path check is the same rule, not a looser
        // one.
        [Test]
        public void Move_Execute_SourceIsTheAllowedRoot_Fails_OwPerm002()
        {
            EnsureFolder(AllowedFolder);
            var destination = ProbeFolder + "/MovedRoot";

            var result = new MoveAssetOperation(() => RestrictedAllowedPaths)
                .Execute(MoveContext(AllowedFolder, destination));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "asset");
            Assert.That(AssetDatabase.IsValidFolder(AllowedFolder), Is.True,
                "a refused move leaves the allowed root in place");
            Assert.That(AssetDatabase.IsValidFolder(destination), Is.False,
                "nothing arrives at the destination either");
        }

        [Test]
        public void Move_Execute_UnresolvableRef_Fails_OwUnity001()
        {
            var context = MoveContextFromRef(
                "{\"kind\":\"asset\",\"path\":\"Assets/OpWeaveAssetMCD/Gone.txt\"}",
                ProbeFolder + "/Dest.txt");

            var result = new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(context);

            AssertFailedWith(result, ErrorCodes.OW_UNITY_001, "asset");
        }

        // The §F5 evidence row: guid, source and destination paths, the
        // empty destination, the content hash and the session id.
        [Test]
        public void Move_Preflight_Snapshot_CarriesTheEvidenceRow()
        {
            var source = ProbeFolder + "/Snap.txt";
            var destination = ProbeFolder + "/SnapMoved.txt";
            var guid = CreateTextAsset(source, "snapshot\n");

            var preflight = new MoveAssetOperation(() => PermissiveAllowedPaths).Preflight(
                MoveContext(source, destination));

            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((string?)snapshot["guid"], Is.EqualTo(guid));
            Assert.That((string?)snapshot["sourcePath"], Is.EqualTo(source));
            Assert.That((string?)snapshot["destination"], Is.EqualTo(destination));
            Assert.That((bool?)snapshot["destinationExisted"], Is.False);
            Assert.That((string?)snapshot["contentHash"], Does.Match("^[0-9a-f]{64}$"));
            Assert.That((string?)snapshot["editorSessionId"], Is.Not.Empty);
            Assert.That(preflight.PlannedChanges, Is.Empty);
        }

        [Test]
        public void Move_Preflight_SkipsPendingInputs()
        {
            var source = ProbeFolder + "/Pending.txt";
            CreateTextAsset(source, "pending\n");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["asset"] = InputValue.Present(Json(AssetRefJson(source))),
                    ["destination"] = InputValue.Missing,
                },
                MoveInputs, MoveOutputs, null,
                new Dictionary<string, string> { ["destination"] = "producer" });

            var preflight = new MoveAssetOperation(() => PermissiveAllowedPaths).Preflight(context);

            Assert.That(preflight.Diagnostics, Is.Empty);
        }

        // ══ unity.asset.move — §F5 Reconcile, six cases ════════════════

        // Case 1 — interrupted before Execute: the guid still sits at the
        // source, the destination is empty and the content matches —
        // provably not applied and safe to re-run.
        [Test]
        public void Move_Reconcile_NotExecuted_ReturnsNotApplied()
        {
            var source = ProbeFolder + "/RecNew.txt";
            var destination = ProbeFolder + "/RecMoved.txt";
            CreateTextAsset(source, "not executed\n");
            var snapshot = MoveSnapshot(source, destination);

            var verdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(
                MoveContext(source, destination), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 2 — executed but StepSucceeded not recorded: the same guid
        // at the destination, nothing at the source, matching content.
        [Test]
        public void Move_Reconcile_ExecutedUnrecorded_ReturnsCompletedWithOutputs()
        {
            var source = ProbeFolder + "/RecDone.txt";
            var destination = ProbeFolder + "/RecMoved.txt";
            var guid = CreateTextAsset(source, "done\n");
            var context = MoveContext(source, destination);
            var snapshot = new MoveAssetOperation(() => PermissiveAllowedPaths).Preflight(context).Snapshot;
            Assert.That(
                new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(MoveContext(source, destination)).Kind,
                Is.EqualTo(StepResultKind.Succeeded),
                "the uninterrupted run must have moved the asset");

            var verdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                () => verdict is ReconcileResult.Unknown u ? u.Reason : null);
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            var asset = (JObject)outputs["asset"].ToToken();
            Assert.That((string?)asset["kind"], Is.EqualTo("asset"));
            Assert.That((string?)asset["path"], Is.EqualTo(destination));
            Assert.That((string?)asset["guid"], Is.EqualTo(guid));
        }

        // Case 3 — partial side effect: the file bytes arrived at the
        // destination but nothing imported them and the guid never moved —
        // neither provably applied nor safely re-runnable.
        [Test]
        public void Move_Reconcile_PartialSideEffect_ReturnsUnknown()
        {
            var source = ProbeFolder + "/Partial.txt";
            var destination = ProbeFolder + "/PartialMoved.txt";
            var guid = CreateTextAsset(source, "partial\n");
            var snapshot = MoveSnapshot(source, destination);
            File.Copy(PhysicalPath(source), PhysicalPath(destination));
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    destination, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.Empty, "the partial fixture must stay un-imported");

            var verdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(
                MoveContext(source, destination), snapshot);

            AssertUnknown(verdict, "occupies the destination");
            Assert.That(File.ReadAllText(PhysicalPath(source)), Is.EqualTo("partial\n"),
                "an Unknown verdict never deletes the artifact it could not prove");
            Assert.That(File.Exists(PhysicalPath(destination)), Is.True,
                "the half-moved file is left for review");
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(source));
        }

        // Case 4 — a pre-existing same-name artifact, both recorded
        // variants: the snapshot that itself saw the destination occupied,
        // and a foreign artifact that arrived after the empty-destination
        // snapshot.
        [Test]
        public void Move_Reconcile_PreexistingArtifact_ReturnsUnknown()
        {
            var source = ProbeFolder + "/PreExisting.txt";
            var destination = ProbeFolder + "/PreExistingMoved.txt";
            CreateTextAsset(source, "pre existing\n");
            var context = MoveContext(source, destination);
            var snapshot = new MoveAssetOperation(() => PermissiveAllowedPaths).Preflight(context).Snapshot;

            // 4a: the snapshot recorded the destination as occupied.
            var seen = (JObject)snapshot.ToToken();
            seen["destinationExisted"] = true;
            CreateTextAsset(destination, "foreign\n");
            var seenVerdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(
                context, JsonValue.From(seen));
            AssertUnknown(seenVerdict, "pre-existing");

            // 4b: the snapshot saw an empty destination, then a foreign
            // asset arrived while the target still sits at the source.
            AssetDatabase.DeleteAsset(destination);
            CreateTextAsset(destination, "foreign\n");
            var foreignVerdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);
            AssertUnknown(foreignVerdict, "occupies the destination");
            Assert.That(File.ReadAllText(PhysicalPath(destination)), Is.EqualTo("foreign\n"),
                "the foreign artifact is never overwritten");
        }

        // Case 5 — the moved artifact was changed after the step ran: its
        // content no longer matches the recorded hash.
        [Test]
        public void Move_Reconcile_ModifiedArtifact_ReturnsUnknown_AndDeletesNothing()
        {
            var source = ProbeFolder + "/Modified.txt";
            var destination = ProbeFolder + "/ModifiedMoved.txt";
            CreateTextAsset(source, "original\n");
            var context = MoveContext(source, destination);
            var snapshot = new MoveAssetOperation(() => PermissiveAllowedPaths).Preflight(context).Snapshot;
            Assert.That(
                new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(MoveContext(source, destination)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            File.WriteAllText(PhysicalPath(destination), "changed by another operation\n");
            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);

            var verdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);

            AssertUnknown(verdict, "content hash");
            Assert.That(
                File.ReadAllText(PhysicalPath(destination)),
                Is.EqualTo("changed by another operation\n"),
                "a modified artifact is left untouched");
        }

        // Case 6 — a simulated Editor restart (different editorSessionId):
        // the file artifact stays re-identifiable by its permanent
        // path/guid/content keys, so the verdict still follows the
        // evidence (§F5 common rule 3).
        [Test]
        public void Move_Reconcile_SimulatedRestart_StillDecidesByEvidence()
        {
            var source = ProbeFolder + "/Restart.txt";
            var destination = ProbeFolder + "/RestartMoved.txt";
            CreateTextAsset(source, "restart\n");
            var context = MoveContext(source, destination);
            var snapshot = new MoveAssetOperation(() => PermissiveAllowedPaths).Preflight(context).Snapshot;
            Assert.That(
                new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(MoveContext(source, destination)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var restarted = (JObject)snapshot.ToToken();
            restarted["editorSessionId"] = "00000000000000000000000000c0ffee";
            var restartSnapshot = JsonValue.From(restarted);

            var verdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, restartSnapshot);
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "a file artifact is re-identifiable across a session boundary");

            Assert.That(AssetDatabase.MoveAsset(destination, source), Is.Empty,
                "the fixture must restore the asset to the source");
            AssetDatabase.Refresh();
            var absent = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, restartSnapshot);
            Assert.That(absent, Is.TypeOf<ReconcileResult.NotApplied>(),
                "an un-applied move stays safely re-runnable after a restart");
        }

        // A guid that resolves at a third path is neither the source nor
        // the destination the snapshot recorded — Unknown.
        [Test]
        public void Move_Reconcile_GuidAtAThirdPath_ReturnsUnknown()
        {
            var source = ProbeFolder + "/Third.txt";
            var destination = ProbeFolder + "/ThirdMoved.txt";
            var elsewhere = ProbeFolder + "/Elsewhere.txt";
            CreateTextAsset(source, "third\n");
            // The context keeps the recorded input Ref; it is built before
            // the fixture mutates the project, as in a persisted run.
            var context = MoveContext(source, destination);
            var snapshot = MoveSnapshot(source, destination);
            Assert.That(AssetDatabase.MoveAsset(source, elsewhere), Is.Empty);

            var verdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);

            AssertUnknown(verdict, "different path");
        }

        // Regression: after the moved asset is deleted, GUIDToAssetPath can
        // keep answering with the destination — a stale mapping is not a
        // live asset, so the verdict stays Unknown.
        [Test]
        public void Move_Reconcile_MovedArtifactDeleted_ReturnsUnknown()
        {
            var source = ProbeFolder + "/MoveGone.txt";
            var destination = ProbeFolder + "/MoveGoneDest.txt";
            CreateTextAsset(source, "stale\n");
            var context = MoveContext(source, destination);
            var snapshot = MoveSnapshot(source, destination);
            Assert.That(
                new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(MoveContext(source, destination)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(AssetDatabase.DeleteAsset(destination), Is.True);

            var verdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);

            AssertUnknown(verdict, "no longer resolves");
        }

        // Evidence that cannot be re-established is Unknown — never
        // Completed, never NotApplied.
        [Test]
        public void Move_Reconcile_MalformedOrMissingEvidence_ReturnsUnknown()
        {
            var source = ProbeFolder + "/Evidence.txt";
            var destination = ProbeFolder + "/EvidenceMoved.txt";
            CreateTextAsset(source, "evidence\n");
            var snapshot = MoveSnapshot(source, destination);
            var context = MoveContext(source, destination);

            Assert.That(
                new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, JsonValue.From(new JValue(5))),
                Is.TypeOf<ReconcileResult.Unknown>(),
                "a non-object snapshot restores no evidence");

            var missing = (JObject)snapshot.ToToken();
            missing.Remove("contentHash");
            AssertUnknown(
                new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, JsonValue.From(missing)),
                "missing");
        }

        // ══ unity.asset.copy — Execute ═════════════════════════════════

        [Test]
        public void Copy_Execute_CopiesAsset_WithFreshGuidAndSameContent()
        {
            var source = ProbeFolder + "/Source.txt";
            var destination = ProbeFolder + "/Copy.txt";
            var sourceGuid = CreateTextAsset(source, "copy me\n");
            var changes = new StepChangeRecorder(0, "cp");
            var context = CopyContext(source, destination, changes);

            var result = new CopyAssetOperation().Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(File.ReadAllText(PhysicalPath(destination)), Is.EqualTo("copy me\n"));
            var copyGuid = AssetDatabase.AssetPathToGUID(
                destination, AssetPathToGUIDOptions.OnlyExistingAssets);
            Assert.That(copyGuid, Is.Not.Empty, "the copy is an imported asset");
            Assert.That(copyGuid, Is.Not.EqualTo(sourceGuid),
                "a copy always gets a fresh guid");
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    source, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.EqualTo(sourceGuid), "the source is untouched");

            var output = (JObject)context.Outputs["asset"].ToToken();
            Assert.That((string?)output["kind"], Is.EqualTo("asset"));
            Assert.That((string?)output["path"], Is.EqualTo(destination));
            Assert.That((string?)output["guid"], Is.EqualTo(copyGuid));
            AssertOutputsMatchDeclaredPortTypes(context, MoveOutputs);

            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Created));
            Assert.That(changes.Records[0].Target.Kind, Is.EqualTo("asset"));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(destination));
            Assert.That(changes.Records[0].Target.Guid, Is.EqualTo(copyGuid));
            Assert.That(changes.Records[0].Undoable, Is.False);
        }

        // A copy only reads the source — a read-only package asset is a
        // legal copy source (SPEC §G7 item 1 is asymmetrical by design).
        [Test]
        public void Copy_Execute_FromReadOnlyPackage_Succeeds()
        {
            var destination = ProbeFolder + "/PackageCopy.json";

            var result = new CopyAssetOperation().Execute(
                CopyContext(PackageAsset, destination));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(File.Exists(PhysicalPath(destination)), Is.True);
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    destination, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.Not.EqualTo(AssetDatabase.AssetPathToGUID(PackageAsset)));
        }

        [Test]
        public void Copy_Execute_ExistingDestination_Fails_OwUnity005_AndLeavesBoth()
        {
            var source = ProbeFolder + "/CopySource.txt";
            var destination = ProbeFolder + "/CopyTaken.txt";
            CreateTextAsset(source, "source\n");
            CreateTextAsset(destination, "kept\n");

            var result = new CopyAssetOperation().Execute(
                CopyContext(source, destination));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "destination");
            Assert.That(File.ReadAllText(PhysicalPath(destination)), Is.EqualTo("kept\n"));

            var preflight = new CopyAssetOperation().Preflight(
                CopyContext(source, destination));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_UNITY_005),
                "Preflight reports the occupied destination too (Dry Run truth)");
        }

        [Test]
        public void Copy_Execute_MissingDestinationFolder_Fails_OwUnity005()
        {
            var source = ProbeFolder + "/CopyOrphan.txt";
            CreateTextAsset(source, "orphan\n");

            var result = new CopyAssetOperation().Execute(
                CopyContext(source, "Assets/OpWeaveAssetMCDNoSuchDir/Copy.txt"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_005, "destination");
        }

        [Test]
        public void Copy_Execute_EscapedDestination_Fails_OwPerm002()
        {
            var source = ProbeFolder + "/CopyEscape.txt";
            CreateTextAsset(source, "escape\n");

            var result = new CopyAssetOperation().Execute(
                CopyContext(source, "Assets/../OpWeaveAssetMCDEscape/Escaped.txt"));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "destination");
        }

        [Test]
        public void Copy_Execute_PackageDestination_Fails_OwUnity010()
        {
            var source = ProbeFolder + "/CopyToPackage.txt";
            CreateTextAsset(source, "to package\n");

            var result = new CopyAssetOperation().Execute(
                CopyContext(source, PackageFolder + "/Injected.json"));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_010, "destination");
        }

        [Test]
        public void Copy_Execute_UnresolvableRef_Fails_OwUnity001()
        {
            var context = CopyContextFromRef(
                "{\"kind\":\"asset\",\"path\":\"Assets/OpWeaveAssetMCD/Gone.txt\"}",
                ProbeFolder + "/Dest.txt");

            var result = new CopyAssetOperation().Execute(context);

            AssertFailedWith(result, ErrorCodes.OW_UNITY_001, "asset");
        }

        // The §F5 evidence row: source guid and content hash, destination
        // and its empty state — plus the session id.
        [Test]
        public void Copy_Preflight_Snapshot_CarriesTheEvidenceRow()
        {
            var source = ProbeFolder + "/CopySnap.txt";
            var destination = ProbeFolder + "/CopySnapDest.txt";
            var guid = CreateTextAsset(source, "snapshot\n");

            var preflight = new CopyAssetOperation().Preflight(
                CopyContext(source, destination));

            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((string?)snapshot["sourceGuid"], Is.EqualTo(guid));
            Assert.That((string?)snapshot["sourcePath"], Is.EqualTo(source));
            Assert.That((string?)snapshot["sourceContentHash"], Does.Match("^[0-9a-f]{64}$"));
            Assert.That((string?)snapshot["destination"], Is.EqualTo(destination));
            Assert.That((bool?)snapshot["destinationExisted"], Is.False);
            Assert.That((string?)snapshot["editorSessionId"], Is.Not.Empty);
            Assert.That(preflight.PlannedChanges, Is.Empty);
        }

        [Test]
        public void Copy_Preflight_SkipsPendingInputs()
        {
            var source = ProbeFolder + "/CopyPending.txt";
            CreateTextAsset(source, "pending\n");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["asset"] = InputValue.Present(Json(AssetRefJson(source))),
                    ["destination"] = InputValue.Missing,
                },
                MoveInputs, MoveOutputs, null,
                new Dictionary<string, string> { ["destination"] = "producer" });

            var preflight = new CopyAssetOperation().Preflight(context);

            Assert.That(preflight.Diagnostics, Is.Empty);
        }

        // ══ unity.asset.copy — §F5 Reconcile, six cases ════════════════

        // Case 1 — interrupted before Execute: nothing at the destination
        // is provably not applied, and re-execution is safe.
        [Test]
        public void Copy_Reconcile_NotExecuted_ReturnsNotApplied()
        {
            var source = ProbeFolder + "/CopyRecNew.txt";
            var destination = ProbeFolder + "/CopyRecDest.txt";
            CreateTextAsset(source, "not executed\n");
            var snapshot = CopySnapshot(source, destination);

            var verdict = new CopyAssetOperation().Reconcile(
                CopyContext(source, destination), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 2 — executed but StepSucceeded not recorded: a new guid at
        // the destination whose content matches the recorded source hash.
        [Test]
        public void Copy_Reconcile_ExecutedUnrecorded_ReturnsCompletedWithOutputs()
        {
            var source = ProbeFolder + "/CopyRecDone.txt";
            var destination = ProbeFolder + "/CopyRecDest.txt";
            var sourceGuid = CreateTextAsset(source, "done\n");
            var context = CopyContext(source, destination);
            var snapshot = new CopyAssetOperation().Preflight(context).Snapshot;
            Assert.That(
                new CopyAssetOperation().Execute(CopyContext(source, destination)).Kind,
                Is.EqualTo(StepResultKind.Succeeded),
                "the uninterrupted run must have produced the copy");

            var verdict = new CopyAssetOperation().Reconcile(context, snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                () => verdict is ReconcileResult.Unknown u ? u.Reason : null);
            var outputs = ((ReconcileResult.Completed)verdict).Outputs;
            var asset = (JObject)outputs["asset"].ToToken();
            Assert.That((string?)asset["kind"], Is.EqualTo("asset"));
            Assert.That((string?)asset["path"], Is.EqualTo(destination));
            Assert.That((string?)asset["guid"], Is.EqualTo(
                AssetDatabase.AssetPathToGUID(
                    destination, AssetPathToGUIDOptions.OnlyExistingAssets)));
            Assert.That((string?)asset["guid"], Is.Not.EqualTo(sourceGuid));
        }

        // Case 3 — partial side effect: the file bytes arrived at the
        // destination but nothing imported them — not a Completed the
        // evidence allows, and not safely re-runnable.
        [Test]
        public void Copy_Reconcile_PartialSideEffect_ReturnsUnknown()
        {
            var source = ProbeFolder + "/CopyPartial.txt";
            var destination = ProbeFolder + "/CopyPartialDest.txt";
            CreateTextAsset(source, "partial\n");
            var snapshot = CopySnapshot(source, destination);
            File.Copy(PhysicalPath(source), PhysicalPath(destination));
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    destination, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.Empty, "the partial fixture must stay un-imported");

            var verdict = new CopyAssetOperation().Reconcile(
                CopyContext(source, destination), snapshot);

            AssertUnknown(verdict, "not an imported asset");
            Assert.That(File.Exists(PhysicalPath(destination)), Is.True,
                "the half-written file is left for review");
        }

        // Case 4 — a pre-existing same-name artifact, both recorded
        // variants.
        [Test]
        public void Copy_Reconcile_PreexistingArtifact_ReturnsUnknown()
        {
            var source = ProbeFolder + "/CopyPreExisting.txt";
            var destination = ProbeFolder + "/CopyPreDest.txt";
            CreateTextAsset(source, "pre existing\n");
            var context = CopyContext(source, destination);
            var snapshot = new CopyAssetOperation().Preflight(context).Snapshot;

            // 4a: the snapshot recorded the destination as occupied.
            var seen = (JObject)snapshot.ToToken();
            seen["destinationExisted"] = true;
            CreateTextAsset(destination, "foreign\n");
            var seenVerdict = new CopyAssetOperation().Reconcile(
                context, JsonValue.From(seen));
            AssertUnknown(seenVerdict, "pre-existing");

            // 4b: the snapshot saw an empty destination, then a foreign
            // artifact whose content does not match arrived.
            AssetDatabase.DeleteAsset(destination);
            CreateTextAsset(destination, "foreign\n");
            var foreignVerdict = new CopyAssetOperation().Reconcile(context, snapshot);
            AssertUnknown(foreignVerdict, "content hash");
            Assert.That(File.ReadAllText(PhysicalPath(destination)), Is.EqualTo("foreign\n"),
                "the foreign artifact is never overwritten");
        }

        // Case 5 — the copy was changed after the step ran: its content no
        // longer matches the recorded hash.
        [Test]
        public void Copy_Reconcile_ModifiedArtifact_ReturnsUnknown_AndDeletesNothing()
        {
            var source = ProbeFolder + "/CopyModified.txt";
            var destination = ProbeFolder + "/CopyModifiedDest.txt";
            CreateTextAsset(source, "original\n");
            var context = CopyContext(source, destination);
            var snapshot = new CopyAssetOperation().Preflight(context).Snapshot;
            Assert.That(
                new CopyAssetOperation().Execute(CopyContext(source, destination)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            File.WriteAllText(PhysicalPath(destination), "changed by another operation\n");
            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);

            var verdict = new CopyAssetOperation().Reconcile(context, snapshot);

            AssertUnknown(verdict, "content hash");
            Assert.That(
                File.ReadAllText(PhysicalPath(destination)),
                Is.EqualTo("changed by another operation\n"),
                "a modified artifact is left untouched");
        }

        // Case 6 — a simulated Editor restart: the file artifact stays
        // re-identifiable by its permanent keys.
        [Test]
        public void Copy_Reconcile_SimulatedRestart_StillDecidesByEvidence()
        {
            var source = ProbeFolder + "/CopyRestart.txt";
            var destination = ProbeFolder + "/CopyRestartDest.txt";
            CreateTextAsset(source, "restart\n");
            var context = CopyContext(source, destination);
            var snapshot = new CopyAssetOperation().Preflight(context).Snapshot;
            Assert.That(
                new CopyAssetOperation().Execute(CopyContext(source, destination)).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var restarted = (JObject)snapshot.ToToken();
            restarted["editorSessionId"] = "00000000000000000000000000c0ffee";
            var restartSnapshot = JsonValue.From(restarted);

            var verdict = new CopyAssetOperation().Reconcile(context, restartSnapshot);
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "a file artifact is re-identifiable across a session boundary");

            Assert.That(AssetDatabase.DeleteAsset(destination), Is.True);
            AssetDatabase.Refresh();
            var absent = new CopyAssetOperation().Reconcile(context, restartSnapshot);
            Assert.That(absent, Is.TypeOf<ReconcileResult.NotApplied>(),
                "an absent destination stays safely re-runnable after a restart");
        }

        // The destination carrying the source's own guid is a move, not
        // the copy the evidence describes — Unknown, never Completed.
        [Test]
        public void Copy_Reconcile_SameGuidAtDestination_ReturnsUnknown()
        {
            var source = ProbeFolder + "/CopyMoved.txt";
            var destination = ProbeFolder + "/CopyMovedDest.txt";
            CreateTextAsset(source, "moved not copied\n");
            var context = CopyContext(source, destination);
            var snapshot = CopySnapshot(source, destination);
            Assert.That(AssetDatabase.MoveAsset(source, destination), Is.Empty);

            var verdict = new CopyAssetOperation().Reconcile(context, snapshot);

            AssertUnknown(verdict, "source's own guid");
        }

        [Test]
        public void Copy_Reconcile_MalformedOrMissingEvidence_ReturnsUnknown()
        {
            var source = ProbeFolder + "/CopyEvidence.txt";
            var destination = ProbeFolder + "/CopyEvidenceDest.txt";
            CreateTextAsset(source, "evidence\n");
            var snapshot = CopySnapshot(source, destination);
            var context = CopyContext(source, destination);

            Assert.That(
                new CopyAssetOperation().Reconcile(context, JsonValue.From(new JValue(5))),
                Is.TypeOf<ReconcileResult.Unknown>(),
                "a non-object snapshot restores no evidence");

            var missing = (JObject)snapshot.ToToken();
            missing.Remove("sourceContentHash");
            AssertUnknown(
                new CopyAssetOperation().Reconcile(context, JsonValue.From(missing)),
                "missing");
        }

        // ══ unity.asset.delete — Execute ═══════════════════════════════

        // mode absent → the §I.3 default 'trash' applies; the asset leaves
        // the project through MoveAssetToTrash.
        [Test]
        public void Delete_Execute_TrashByDefault()
        {
            var path = ProbeFolder + "/Trash.txt";
            var guid = CreateTextAsset(path, "to trash\n");
            var changes = new StepChangeRecorder(0, "del");
            var context = DeleteContext(path, null, changes);

            var result = new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(context);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(GuidMapsToLiveAsset(guid), Is.False,
                "a trashed asset's guid no longer resolves to a live asset");
            Assert.That(File.Exists(PhysicalPath(path)), Is.False);

            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Deleted));
            Assert.That(changes.Records[0].Target.Kind, Is.EqualTo("asset"));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(path));
            Assert.That(changes.Records[0].Target.Guid, Is.EqualTo(guid));
            Assert.That(changes.Records[0].Undoable, Is.False);
        }

        [Test]
        public void Delete_Execute_PermanentMode_RemovesTheAsset()
        {
            var path = ProbeFolder + "/Gone.txt";
            var guid = CreateTextAsset(path, "permanent\n");

            var result = new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(
                DeleteContext(path, "permanent"));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(GuidMapsToLiveAsset(guid), Is.False);
            Assert.That(File.Exists(PhysicalPath(path)), Is.False);
            Assert.That(File.Exists(PhysicalPath(path) + ".meta"), Is.False,
                "permanent delete removes the .meta too");
        }

        // A folder is an asset too: the same trash-by-default row applies.
        [Test]
        public void Delete_Execute_Folder_ToTrash()
        {
            var folder = ProbeFolder + "/Doomed";
            EnsureFolder(folder);
            CreateTextAsset(folder + "/Inside.txt", "inside\n");

            var result = new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(DeleteContext(folder));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded), () => Describe(result));
            Assert.That(AssetDatabase.IsValidFolder(folder), Is.False);
            Assert.That(Directory.Exists(PhysicalPath(folder)), Is.False);
        }

        [Test]
        public void Delete_Execute_InvalidMode_Fails_OwOp006()
        {
            var path = ProbeFolder + "/BadMode.txt";
            CreateTextAsset(path, "bad mode\n");

            var result = new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(
                DeleteContext(path, "annihilate"));

            AssertFailedWith(result, ErrorCodes.OW_OP_006, "mode");
            Assert.That(File.Exists(PhysicalPath(path)), Is.True,
                "a refused mode deletes nothing");
        }

        // Deleting inside a read-only package is a write inside it —
        // refused before anything is removed (SPEC §G7 item 1).
        [Test]
        public void Delete_Execute_PackageAsset_Fails_OwUnity010()
        {
            var result = new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(
                DeleteContext(PackageAsset));

            AssertFailedWith(result, ErrorCodes.OW_UNITY_010, "asset");
            Assert.That(
                AssetDatabase.AssetPathToGUID(PackageAsset), Is.Not.Empty,
                "the read-only package asset must not have been deleted");
        }

        // §G7 item 1 regression: the delete target is a write at its own
        // path, so an AssetRef outside the Run's narrowed allowedPaths is
        // OW-PERM-002 before anything is removed.
        [Test]
        public void Delete_Execute_TargetOutsideAllowedPaths_Fails_OwPerm002()
        {
            var path = ProbeFolder + "/OutsideDelete.txt";
            CreateTextAsset(path, "outside\n");

            var result = new DeleteAssetOperation(() => RestrictedAllowedPaths)
                .Execute(DeleteContext(path));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "asset");
            Assert.That(File.Exists(PhysicalPath(path)), Is.True,
                "a refused delete leaves the asset in place");
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.Not.Empty);
            Assert.That(result.Error!.Message, Does.Not.Contain(path),
                "the refused path is never echoed by the permission refusal");

            var preflight = new DeleteAssetOperation(() => RestrictedAllowedPaths)
                .Preflight(DeleteContext(path));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_PERM_002),
                "Preflight reports the outside target too (Dry Run truth)");
        }

        // The same narrowed bound admits the delete of a target it covers.
        [Test]
        public void Delete_Execute_TargetInsideAllowedPaths_StillDeletes()
        {
            var path = AllowedFolder + "/InsideDelete.txt";
            EnsureFolder(AllowedFolder);
            CreateTextAsset(path, "inside\n");

            var result = new DeleteAssetOperation(() => RestrictedAllowedPaths)
                .Execute(DeleteContext(path, "permanent"));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded),
                () => Describe(result));
            Assert.That(File.Exists(PhysicalPath(path)), Is.False);
        }

        // The F3 fail-closed half (SPEC §G7 item 1, §H4.1 decision D-R6):
        // a missing snapshot is never "everything is allowed" — the frozen
        // boundary a Run was accepted under cannot be produced, so the
        // resolved target is OW-PERM-002 before anything is removed. This
        // is the state a re-attached Run is in after a domain reload or
        // Editor restart, when the accepted-settings entry lives only in
        // the previous domain's memory.
        [Test]
        public void Delete_Execute_NullSnapshot_Fails_OwPerm002_AndDeletesNothing()
        {
            var path = ProbeFolder + "/NoSnapshotDelete.txt";
            CreateTextAsset(path, "no snapshot\n");

            var result = new DeleteAssetOperation(() => null)
                .Execute(DeleteContext(path, "permanent"));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "asset");
            Assert.That(File.Exists(PhysicalPath(path)), Is.True,
                "a refused delete leaves the asset in place");
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.Not.Empty);

            var preflight = new DeleteAssetOperation(() => null)
                .Preflight(DeleteContext(path));
            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_PERM_002),
                "Preflight reports the missing boundary too (Dry Run truth)");
        }

        // The declared path_writable rule counts a path inside an allowed
        // set only when it begins with an allowed root plus '/': the root
        // folder itself is outside, so deleting the allowed root is
        // refused — the resolved-path check is the same rule, not a looser
        // one.
        [Test]
        public void Delete_Execute_TargetIsTheAllowedRoot_Fails_OwPerm002()
        {
            EnsureFolder(AllowedFolder);
            CreateTextAsset(AllowedFolder + "/Inside.txt", "inside\n");

            var result = new DeleteAssetOperation(() => RestrictedAllowedPaths)
                .Execute(DeleteContext(AllowedFolder, "permanent"));

            AssertFailedWith(result, ErrorCodes.OW_PERM_002, "asset");
            Assert.That(AssetDatabase.IsValidFolder(AllowedFolder), Is.True,
                "a refused delete leaves the allowed root in place");
        }

        // The recovered-Run half of the F3 finding (SPEC §F4, §G7 item 1):
        // a Run re-attached after a domain reload or Editor restart has no
        // accepted-settings snapshot — the frozen boundary it was admitted
        // under lives in the previous domain's memory only, and nothing
        // durable carries it. Its re-attachment instead records the
        // settings this domain's load validates, so the resumed delete
        // step is still judged by a boundary that excludes the target:
        // OW-PERM-002 on port 'asset' before any mutation, never
        // "everything is allowed".
        [Test]
        public void Delete_RecoveredRun_TargetOutsideBoundary_Fails_OwPerm002()
        {
            EnsureFolder(AllowedFolder);
            var target = ProbeFolder + "/RecoveredDelete.txt";
            var guid = CreateTextAsset(target, "recover me\n");
            var storeRoot = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library",
                "OpWeaveAssetMCDRecovery", "t" + Guid.NewGuid().ToString("N"));
            RunnerHostFixture? first = null;
            RunnerHostFixture? second = null;
            try
            {
                // Accepted under a narrowed allowedPaths — the probe folder
                // is outside — and left mid-flight at a held waitable, so
                // the delete step never ran in this domain. FlushJournal is
                // the quitting-time write of the Buffered records (SPEC
                // §F3 item 1); Dispose drops the domain's exclusions and
                // every in-memory accepted-settings entry with it.
                first = new RunnerHostFixture(storeRoot);
                first.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                    "{\"version\":2,\"allowedPaths\":[\"" +
                    AllowedFolder + "/\"]}");
                var firstHost = first.Host;
                first.Register(
                    new RunnerHostFixture.ControlledWaitableOperation(
                        "test.mcd.hold"), 'b');
                first.Register(
                    new DeleteAssetOperation(
                        () => firstHost.ActiveRunAllowedPaths), 'd');
                var accepted = first.Host.AcceptRun(
                    RecoveryPlan(target, guid), RecoveryDoc(),
                    ActorKind.Agent, RunnerHostFixture.Options());
                Assert.That(accepted.Ok, Is.True,
                    () => string.Join("; ", accepted.Diagnostics.Select(
                        d => d.Code + " " + d.Message)));
                var runId = accepted.Value!.RunId;
                var tick = first.Host.Tick();
                Assert.That(tick.Ok, Is.True,
                    () => string.Join("; ", tick.Diagnostics.Select(
                        d => d.Code + " " + d.Message)));
                Assert.That(first.Host.Coordinator.StateOf(runId),
                    Is.EqualTo(RunState.Running),
                    "the held waitable keeps the Run mid-flight");
                Assert.That(first.Host.FlushJournal().Ok, Is.True,
                    "the quitting-time flush must persist the buffered records");
                first.Dispose();
                first = null;

                // The next domain: the same store, a new host — the
                // recovery pass re-attaches the unfinished Run and
                // records the settings its own load validates, so the
                // resumed Run is judged by this domain's boundary — the
                // same narrowed file the first domain admitted it under,
                // never the previous domain's in-memory entry. The delete
                // op is wired the way production wires it: the Run's own
                // frozen snapshot through the host accessor.
                second = new RunnerHostFixture(storeRoot);
                second.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                    "{\"version\":2,\"allowedPaths\":[\"" +
                    AllowedFolder + "/\"]}");
                var secondHost = second.Host;
                second.Register(
                    new RunnerHostFixture.ControlledWaitableOperation(
                        "test.mcd.hold") { Release = true }, 'b');
                second.Register(
                    new DeleteAssetOperation(
                        () => secondHost.ActiveRunAllowedPaths), 'd');
                var recovered = second.Host.RecoverRuns();
                Assert.That(recovered.Ok, Is.True,
                    () => string.Join("; ", recovered.Diagnostics.Select(
                        d => d.Code + " " + d.Message)));
                Assert.That(recovered.Value!.ExclusionHeld, Is.True,
                    "the recovery pass must own the exclusion before resuming");
                RunRecoveryOutcome? outcome = null;
                foreach (var entry in recovered.Value.Runs)
                {
                    if (string.Equals(entry.RunId, runId, StringComparison.Ordinal))
                    {
                        outcome = entry.Outcome;
                    }
                }
                Assert.That(outcome, Is.EqualTo(RunRecoveryOutcome.Resumed),
                    "the unfinished mutating Run is resumed on its verified journal");
                Assert.That(second.Host.Coordinator.StateOf(runId),
                    Is.EqualTo(RunState.Running),
                    "the unfinished Run is re-attached and resumed");

                var permRefusal = false;
                for (var i = 0; i < 8 &&
                    second.Host.Coordinator.StateOf(runId) == RunState.Running;
                    i++)
                {
                    second.Clock.Advance(TimeSpan.FromMilliseconds(150));
                    tick = second.Host.Tick();
                    permRefusal |= tick.Diagnostics.Any(
                        d => d.Code == ErrorCodes.OW_PERM_002);
                }
                Assert.That(second.Host.Coordinator.StateOf(runId),
                    Is.EqualTo(RunState.Failed),
                    "the refused delete step fails the Run");
                Assert.That(permRefusal, Is.True,
                    "the resumed delete step is refused with OW-PERM-002");
                Assert.That(
                    RunnerHostFixture.JournalHasAll(
                        storeRoot, runId,
                        "\"type\":\"step_failed\"", "\"step\":1",
                        ErrorCodes.OW_PERM_002),
                    Is.True,
                    "the refusal is journaled on the delete step");
                Assert.That(File.Exists(PhysicalPath(target)), Is.True,
                    "the refused delete removes nothing");
                Assert.That(GuidMapsToLiveAsset(guid), Is.True);
            }
            finally
            {
                second?.Dispose();
                first?.Dispose();
                if (Directory.Exists(storeRoot))
                {
                    try { Directory.Delete(storeRoot, recursive: true); }
                    catch (Exception) { /* a held file stays for the next run */ }
                }
            }
        }

        // The two-step plan the recovered-Run regression runs: a held
        // waitable keeps the Run mid-flight across the simulated domain
        // loss, then the delete step targets the out-of-boundary asset.
        private static CompiledPlan RecoveryPlan(string target, string guid) =>
            RunnerHostFixture.Plan(
                "example.mcd.delete.recovery",
                new[]
                {
                    RunnerHostFixture.Step(
                        0, "hold", "test.mcd.hold", 'b', next: 1,
                        idempotency: Idempotency.Idempotent),
                    RunnerHostFixture.Step(
                        1, "delete", DeleteAssetOperation.OperationId, 'd',
                        inputs: new Dictionary<string, InputSource>
                        {
                            [DeleteAssetOperation.AssetPort] =
                                new Constant(Json(AssetRefJson(guid, target))),
                        },
                        idempotency: Idempotency.Reconcilable),
                });

        private static WorkflowDocument RecoveryDoc() =>
            RunnerHostFixture.Doc(
                "example.mcd.delete.recovery",
                new[]
                {
                    new NodeData
                    {
                        Id = "hold", Operation = "test.mcd.hold", Version = 1,
                    },
                    new NodeData
                    {
                        Id = "delete",
                        Operation = DeleteAssetOperation.OperationId,
                        Version = 1,
                    },
                },
                new[] { new ControlEdge("hold", "delete") });

        [Test]
        public void Delete_Execute_UnresolvableRef_Fails_OwUnity001()
        {
            var context = DeleteContextFromRef(
                "{\"kind\":\"asset\",\"path\":\"Assets/OpWeaveAssetMCD/Gone.txt\"}");

            var result = new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(context);

            AssertFailedWith(result, ErrorCodes.OW_UNITY_001, "asset");
        }

        // The §F5 evidence row: guid, path, content hash, mode, the
        // sibling list and the session id.
        [Test]
        public void Delete_Preflight_Snapshot_CarriesTheEvidenceRow()
        {
            var path = ProbeFolder + "/DeleteSnap.txt";
            var guid = CreateTextAsset(path, "snapshot\n");
            var sibling = ProbeFolder + "/Neighbour.txt";
            var siblingGuid = CreateTextAsset(sibling, "neighbour\n");

            var preflight = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(DeleteContext(path));

            Assert.That(preflight.Diagnostics, Is.Empty,
                () => string.Join("; ", preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((string?)snapshot["guid"], Is.EqualTo(guid));
            Assert.That((string?)snapshot["path"], Is.EqualTo(path));
            Assert.That((string?)snapshot["contentHash"], Does.Match("^[0-9a-f]{64}$"));
            Assert.That((string?)snapshot["mode"], Is.EqualTo("trash"));
            var siblings = (JObject)snapshot["siblings"]!;
            Assert.That(siblings, Is.Not.Null, "the snapshot carries the sibling evidence");
            Assert.That((string?)siblings["Neighbour.txt"], Is.EqualTo(siblingGuid),
                "a sibling is recorded by file name and guid");
            Assert.That(siblings.ContainsKey("DeleteSnap.txt"), Is.False,
                "the target itself is not a sibling");
            Assert.That((string?)snapshot["editorSessionId"], Is.Not.Empty);
            Assert.That(preflight.PlannedChanges, Is.Empty);
        }

        // The explicit mode value is persisted verbatim: a permanent-mode
        // run reconciles against 'permanent', not the default.
        [Test]
        public void Delete_Preflight_Snapshot_RecordsThePermanentMode()
        {
            var path = ProbeFolder + "/DeleteSnapMode.txt";
            CreateTextAsset(path, "snapshot mode\n");

            var preflight = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(
                DeleteContext(path, "permanent"));

            var snapshot = (JObject)preflight.Snapshot.ToToken();
            Assert.That((string?)snapshot["mode"], Is.EqualTo("permanent"));
        }

        [Test]
        public void Delete_Preflight_SkipsPendingInputs()
        {
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["asset"] = InputValue.Missing,
                },
                DeleteInputs, NoOutputs, null,
                new Dictionary<string, string> { ["asset"] = "producer" });

            var preflight = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(context);

            Assert.That(preflight.Diagnostics, Is.Empty);
        }

        // ══ unity.asset.delete — §F5 Reconcile, six cases ══════════════

        // Case 1 — interrupted before Execute: the same guid at the same
        // path with the same content — provably not applied.
        [Test]
        public void Delete_Reconcile_NotExecuted_ReturnsNotApplied()
        {
            var path = ProbeFolder + "/DeleteRecNew.txt";
            CreateTextAsset(path, "not executed\n");
            var snapshot = DeleteSnapshot(path);

            var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(
                DeleteContext(path), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.NotApplied>());
        }

        // Case 2 — executed but StepSucceeded not recorded: the guid no
        // longer resolves, nothing occupies the path and every recorded
        // sibling is still in place.
        [Test]
        public void Delete_Reconcile_ExecutedUnrecorded_ReturnsCompleted()
        {
            var path = ProbeFolder + "/DeleteRecDone.txt";
            var sibling = ProbeFolder + "/DeleteNeighbour.txt";
            CreateTextAsset(path, "done\n");
            CreateTextAsset(sibling, "neighbour\n");
            var context = DeleteContext(path);
            var snapshot = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(context).Snapshot;
            Assert.That(
                new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(DeleteContext(path, "permanent")).Kind,
                Is.EqualTo(StepResultKind.Succeeded),
                "the uninterrupted run must have deleted the asset");

            var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                () => verdict is ReconcileResult.Unknown u ? u.Reason : null);
            Assert.That(((ReconcileResult.Completed)verdict).Outputs, Is.Empty,
                "the Operation declares no outputs (SPEC §I.3)");
            Assert.That(File.Exists(PhysicalPath(sibling)), Is.True,
                "the recorded sibling is untouched");
        }

        // Case 3 — partial side effect: the file is gone from disk but the
        // .meta lingers un-refreshed — whether the database still maps the
        // guid or has already dropped it, neither provably applied (an
        // artifact still occupies the path) nor provably absent (the
        // recorded content cannot be re-read) holds.
        [Test]
        public void Delete_Reconcile_PartialSideEffect_ReturnsUnknown()
        {
            var path = ProbeFolder + "/DeletePartial.txt";
            CreateTextAsset(path, "partial\n");
            var snapshot = DeleteSnapshot(path);
            File.Delete(PhysicalPath(path));
            Assert.That(File.Exists(PhysicalPath(path) + ".meta"), Is.True,
                "without a refresh the .meta outlives the file");

            var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(
                DeleteContext(path), snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Unknown>(),
                () => verdict is ReconcileResult.Unknown u ? u.Reason : verdict.GetType().Name);
            Assert.That(File.Exists(PhysicalPath(path) + ".meta"), Is.True,
                "an Unknown verdict never removes the artifact it could not prove");
        }

        // Case 4 — a pre-existing same-name artifact: the step's target is
        // provably gone (guid unresolvable), yet something else sits at
        // the recorded path — the evidence cannot tell whether the step
        // deleted only its target.
        [Test]
        public void Delete_Reconcile_PreexistingArtifact_ReturnsUnknown()
        {
            var path = ProbeFolder + "/DeletePre.txt";
            var foreign = ProbeFolder + "/DeletePreForeign.txt";
            CreateTextAsset(path, "target\n");
            var context = DeleteContext(path);
            var snapshot = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(context).Snapshot;
            var recordedGuid = (string)((JObject)snapshot.ToToken())["guid"]!;
            Assert.That(
                new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(DeleteContext(path, "permanent")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            // A file re-imported at the deleted asset's path can inherit
            // the recorded guid, which would read as a content change on
            // the target instead of a foreign artifact. The foreign file
            // is created beside the path — where its import must take a
            // fresh guid — and moved onto it so the recorded path is
            // occupied by a provably different guid.
            var foreignGuid = CreateTextAsset(foreign, "foreign\n");
            Assert.That(foreignGuid, Is.Not.EqualTo(recordedGuid),
                "the foreign artifact must not reuse the recorded guid");
            Assert.That(AssetDatabase.MoveAsset(foreign, path), Is.Empty);
            Assert.That(
                AssetDatabase.AssetPathToGUID(
                    path, AssetPathToGUIDOptions.OnlyExistingAssets),
                Is.EqualTo(foreignGuid),
                "the recorded path must be occupied by the foreign guid");

            var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);

            AssertUnknown(verdict, "occupies the recorded path");
            Assert.That(File.ReadAllText(PhysicalPath(path)), Is.EqualTo("foreign\n"),
                "the foreign artifact is never removed");
        }

        // Case 5 — the target was modified after the snapshot recorded its
        // hash: the artifact at the path is no longer the one the step was
        // asked to delete.
        [Test]
        public void Delete_Reconcile_ModifiedArtifact_ReturnsUnknown_AndDeletesNothing()
        {
            var path = ProbeFolder + "/DeleteModified.txt";
            CreateTextAsset(path, "original\n");
            var snapshot = DeleteSnapshot(path);
            File.WriteAllText(PhysicalPath(path), "changed by another operation\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(
                DeleteContext(path), snapshot);

            AssertUnknown(verdict, "content hash");
            Assert.That(
                File.ReadAllText(PhysicalPath(path)),
                Is.EqualTo("changed by another operation\n"),
                "a modified artifact is never deleted");
        }

        // Case 6 — a simulated Editor restart: the durable guid/path/
        // content/sibling evidence still decides the outcome.
        [Test]
        public void Delete_Reconcile_SimulatedRestart_StillDecidesByEvidence()
        {
            var path = ProbeFolder + "/DeleteRestart.txt";
            var sibling = ProbeFolder + "/DeleteRestartNeighbour.txt";
            CreateTextAsset(path, "restart\n");
            CreateTextAsset(sibling, "neighbour\n");
            var context = DeleteContext(path);
            var snapshot = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(context).Snapshot;
            Assert.That(
                new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(DeleteContext(path, "permanent")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var restarted = (JObject)snapshot.ToToken();
            restarted["editorSessionId"] = "00000000000000000000000000c0ffee";
            var restartSnapshot = JsonValue.From(restarted);

            var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, restartSnapshot);
            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                "file artifacts are re-identifiable across a session boundary");
        }

        // A recorded sibling that vanished between Preflight and Reconcile
        // means "other assets are not gone" cannot be proven — Unknown.
        [Test]
        public void Delete_Reconcile_SiblingRemoved_ReturnsUnknown()
        {
            var path = ProbeFolder + "/DeleteSib.txt";
            var sibling = ProbeFolder + "/DeleteSibling.txt";
            CreateTextAsset(path, "target\n");
            CreateTextAsset(sibling, "sibling\n");
            var context = DeleteContext(path);
            var snapshot = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(context).Snapshot;
            Assert.That(
                new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(DeleteContext(path, "permanent")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(AssetDatabase.DeleteAsset(sibling), Is.True);

            var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);

            AssertUnknown(verdict, "beside the recorded target");
        }

        // A guid that survives at a different path is a moved asset, not a
        // deleted one — Unknown, never Completed.
        [Test]
        public void Delete_Reconcile_GuidAtADifferentPath_ReturnsUnknown()
        {
            var path = ProbeFolder + "/DeleteMoved.txt";
            var moved = ProbeFolder + "/DeleteMovedAway.txt";
            CreateTextAsset(path, "moved not deleted\n");
            var context = DeleteContext(path);
            var snapshot = DeleteSnapshot(path);
            Assert.That(AssetDatabase.MoveAsset(path, moved), Is.Empty);

            var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);

            AssertUnknown(verdict, "different path");
            Assert.That(File.Exists(PhysicalPath(moved)), Is.True,
                "the moved artifact is never deleted");
        }

        // Regression: GUIDToAssetPath can keep answering with the deleted
        // asset's former path; the verdict must still be Completed from
        // the live-asset evidence.
        [Test]
        public void Delete_Reconcile_StaleGuidMapping_StillCompletes()
        {
            var path = ProbeFolder + "/DeleteStale.txt";
            var guid = CreateTextAsset(path, "stale\n");
            var context = DeleteContext(path);
            var snapshot = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(context).Snapshot;
            Assert.That(
                new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(DeleteContext(path, "permanent")).Kind,
                Is.EqualTo(StepResultKind.Succeeded));

            var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, snapshot);

            Assert.That(verdict, Is.TypeOf<ReconcileResult.Completed>(),
                () => "a stale guid mapping ('" + AssetDatabase.GUIDToAssetPath(guid) +
                    "') never masks a real deletion: " +
                    (verdict is ReconcileResult.Unknown u ? u.Reason : verdict.GetType().Name));
            Assert.That(GuidMapsToLiveAsset(guid), Is.False);
        }

        [Test]
        public void Delete_Reconcile_MalformedOrMissingEvidence_ReturnsUnknown()
        {
            var path = ProbeFolder + "/DeleteEvidence.txt";
            CreateTextAsset(path, "evidence\n");
            var snapshot = DeleteSnapshot(path);
            var context = DeleteContext(path);

            Assert.That(
                new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, JsonValue.From(new JValue(5))),
                Is.TypeOf<ReconcileResult.Unknown>(),
                "a non-object snapshot restores no evidence");

            var missing = (JObject)snapshot.ToToken();
            missing.Remove("siblings");
            AssertUnknown(
                new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, JsonValue.From(missing)),
                "missing");

            var missingHash = (JObject)snapshot.ToToken();
            missingHash.Remove("contentHash");
            AssertUnknown(
                new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(context, JsonValue.From(missingHash)),
                "missing");
        }

        // ══ [UnityTest] across a real domain reload (SPEC §T.3) ════════

        private static object? s_domainSentinel;
        private static void ArmSentinel() => s_domainSentinel = new object();
        private static bool SentinelAlive => s_domainSentinel is not null;

        // T-W2.4-09: a per-Editor-session token phase-1 stores in SessionState
        // and writes into a per-test session probe file beside the persisted
        // snapshot (a single JSON document that cannot carry the token);
        // phase-2 reads both to prove the persisted state belongs to this
        // session's phase-1 rather than residue of an earlier run — or of a
        // SIBLING reload test in this fixture, which shares the same
        // SessionState (the ChangeSetDomainReloadTests pattern).
        private const string ProbeTokenKey = "OpWeave.W2409.AssetMCD.ProbeToken";

        // Bound on phase-1 re-drive legs after a predecessor's stale resume is
        // detected. leg is a method local, so a real reload resets it; the
        // this-session probe check is what actually ends the loop.
        private const int MaxReDriveLegs = 3;

        private static void Crumb(string message) =>
            UnityEngine.Debug.Log("[W2409-assetmcd] " + message);

        // A bare RecompileScripts with no script change produces no
        // domain reload: a temporary marker script under Assets makes the
        // compile real (the AssetCreateOperationTests pattern); the Unity
        // setup/teardown removes it and recompiles once more.
        private static void WriteReloadMarker()
        {
            Directory.CreateDirectory(MarkerDisk);
            File.WriteAllText(
                Path.Combine(MarkerDisk, "ReloadMarker.cs"), MarkerSource);
            AssetDatabase.Refresh();
        }

        private static string SnapshotStatePath(string name) =>
            Path.Combine(ProbeStateDir, name + ".json");

        private static string SessionStatePath(string name) =>
            Path.Combine(ProbeStateDir, name + ".session.txt");

        // The phase-1 probe proves THIS test's phase-1 ran in THIS Editor
        // session: the snapshot file must exist AND its per-test session file
        // must equal the token phase-1 stored in SessionState. Both Library/
        // and SessionState survive the reload, so a stale-resume hijack
        // (phase-1 skipped: no file, no token) and residue of an earlier
        // session — or of a sibling reload test — fail the check and the
        // caller re-drives a real phase-1 instead of skipping into phase-2.
        private static bool ProbeWrittenThisSession(string name)
        {
            if (!File.Exists(SnapshotStatePath(name)) ||
                !File.Exists(SessionStatePath(name)))
            {
                return false;
            }
            var expectedToken = SessionState.GetString(ProbeTokenKey, "");
            return expectedToken.Length > 0 &&
                File.ReadAllText(SessionStatePath(name)).Trim() == expectedToken;
        }

        // A stale-resume re-drive repeats Execute on the same paths, so the
        // artifacts a hijacked or abandoned earlier leg left behind must go
        // first — plus the marker, whose byte-identical rewrite would
        // otherwise queue no compile. The probe folder is recreated empty;
        // the caller then re-creates its fixture asset before phase-1.
        private static void CleanupForReDrive()
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
            CleanupMarker();
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

        private static string DescribeVerdict(ReconcileResult verdict) =>
            verdict is ReconcileResult.Unknown unknown
                ? "Unknown(" + unknown.Reason + ")"
                : verdict.GetType().Name;

        // ── move reload ────────────────────────────────────────────────

        private static PhaseResult MoveBeforeReload()
        {
            try
            {
                var source = ProbeFolder + "/ReloadMove.txt";
                var destination = ProbeFolder + "/ReloadMoved.txt";
                var context = MoveContext(source, destination);
                var preflight = new MoveAssetOperation(() => PermissiveAllowedPaths).Preflight(context);
                if (preflight.Diagnostics.Count != 0)
                {
                    return PhaseResult.Fail("preflight diagnostics: " + string.Join("; ",
                        preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
                }
                var executed = new MoveAssetOperation(() => PermissiveAllowedPaths).Execute(context);
                if (executed.Kind != StepResultKind.Succeeded)
                {
                    return PhaseResult.Fail("execute failed: " + Describe(executed));
                }
                Directory.CreateDirectory(ProbeStateDir);
                File.WriteAllText(SnapshotStatePath("move"), preflight.Snapshot.CanonicalText);
                var token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
                File.WriteAllText(SessionStatePath("move"), token + "\n");
                WriteReloadMarker();
                ArmSentinel();
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-1 threw: " + e.Message);
            }
        }

        private static PhaseResult MoveAfterReload()
        {
            try
            {
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(SnapshotStatePath("move")))
                {
                    return PhaseResult.Fail(
                        expectedToken.Length == 0
                            ? "phase-1 never executed in this Editor session (" +
                                SnapshotStatePath("move") + " was never written; " +
                                "probeDir exists=" + Directory.Exists(ProbeStateDir) + ")"
                            : "the persisted snapshot " + SnapshotStatePath("move") +
                                " was deleted after phase-1 wrote it");
                }
                if (expectedToken.Length == 0 || !File.Exists(SessionStatePath("move")) ||
                    File.ReadAllText(SessionStatePath("move")).Trim() != expectedToken)
                {
                    return PhaseResult.Fail(
                        "the persisted probe state is stale — a previous Editor " +
                        "session wrote " + SessionStatePath("move"));
                }
                var parsed = JsonValue.Parse(File.ReadAllText(SnapshotStatePath("move")));
                if (!parsed.Ok || parsed.Value is null)
                {
                    return PhaseResult.Fail("persisted snapshot did not parse");
                }
                // The step's recorded input Ref survives the reload; it is
                // not re-resolved after the asset has moved.
                var evidence = (JObject)parsed.Value.ToToken();
                var destination = ProbeFolder + "/ReloadMoved.txt";
                var verdict = new MoveAssetOperation(() => PermissiveAllowedPaths).Reconcile(
                    MoveContextFromRef(
                        AssetRefJson(
                            (string)evidence["guid"]!, (string)evidence["sourcePath"]!),
                        destination),
                    parsed.Value);
                if (verdict is not ReconcileResult.Completed completed)
                {
                    return PhaseResult.Fail("expected Completed, got " + DescribeVerdict(verdict));
                }
                var asset = (JObject)completed.Outputs["asset"].ToToken();
                if ((string?)asset["path"] != destination ||
                    (string?)asset["guid"] != AssetDatabase.AssetPathToGUID(
                        destination, AssetPathToGUIDOptions.OnlyExistingAssets))
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
        public IEnumerator Move_Reconcile_AfterRealDomainReload_ReturnsCompleted()
        {
            CreateTextAsset(ProbeFolder + "/ReloadMove.txt", "move reload\n");
            var before = MoveBeforeReload();
            Crumb("move phase-1 returned ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            // T-W2.4-09 stale-resume self-heal: a predecessor's captured UTF
            // resume PC applies to this fresh enumerator and lands it at one
            // of the two post-yield states inside this loop — with every
            // local (leg included) reset to default. Both continuations
            // drain, then MUST pass the this-session probe check: a probe
            // phase-1 never wrote re-drives a real phase-1 + compile + reload
            // instead of skipping silently into phase-2. If the probe still
            // cannot be produced after the bound, the loop exits and phase-2's
            // existing never-executed diagnostic fails loudly.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (SentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(SentinelAlive,
                    "the recompile did not produce a domain reload");

                if (ProbeWrittenThisSession("move") || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupForReDrive();
                CreateTextAsset(ProbeFolder + "/ReloadMove.txt", "move reload\n");
                var redrive = MoveBeforeReload();
                Crumb("move phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = MoveAfterReload();
            Crumb("move phase-2 returned ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // ── copy reload ────────────────────────────────────────────────

        private static PhaseResult CopyBeforeReload()
        {
            try
            {
                var source = ProbeFolder + "/ReloadCopy.txt";
                var destination = ProbeFolder + "/ReloadCopyDest.txt";
                var context = CopyContext(source, destination);
                var preflight = new CopyAssetOperation().Preflight(context);
                if (preflight.Diagnostics.Count != 0)
                {
                    return PhaseResult.Fail("preflight diagnostics: " + string.Join("; ",
                        preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
                }
                var executed = new CopyAssetOperation().Execute(context);
                if (executed.Kind != StepResultKind.Succeeded)
                {
                    return PhaseResult.Fail("execute failed: " + Describe(executed));
                }
                Directory.CreateDirectory(ProbeStateDir);
                File.WriteAllText(SnapshotStatePath("copy"), preflight.Snapshot.CanonicalText);
                var token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
                File.WriteAllText(SessionStatePath("copy"), token + "\n");
                WriteReloadMarker();
                ArmSentinel();
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-1 threw: " + e.Message);
            }
        }

        private static PhaseResult CopyAfterReload()
        {
            try
            {
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(SnapshotStatePath("copy")))
                {
                    return PhaseResult.Fail(
                        expectedToken.Length == 0
                            ? "phase-1 never executed in this Editor session (" +
                                SnapshotStatePath("copy") + " was never written; " +
                                "probeDir exists=" + Directory.Exists(ProbeStateDir) + ")"
                            : "the persisted snapshot " + SnapshotStatePath("copy") +
                                " was deleted after phase-1 wrote it");
                }
                if (expectedToken.Length == 0 || !File.Exists(SessionStatePath("copy")) ||
                    File.ReadAllText(SessionStatePath("copy")).Trim() != expectedToken)
                {
                    return PhaseResult.Fail(
                        "the persisted probe state is stale — a previous Editor " +
                        "session wrote " + SessionStatePath("copy"));
                }
                var parsed = JsonValue.Parse(File.ReadAllText(SnapshotStatePath("copy")));
                if (!parsed.Ok || parsed.Value is null)
                {
                    return PhaseResult.Fail("persisted snapshot did not parse");
                }
                var source = ProbeFolder + "/ReloadCopy.txt";
                var destination = ProbeFolder + "/ReloadCopyDest.txt";
                var verdict = new CopyAssetOperation().Reconcile(
                    CopyContext(source, destination), parsed.Value);
                if (verdict is not ReconcileResult.Completed completed)
                {
                    return PhaseResult.Fail("expected Completed, got " + DescribeVerdict(verdict));
                }
                var asset = (JObject)completed.Outputs["asset"].ToToken();
                if ((string?)asset["path"] != destination ||
                    (string?)asset["guid"] != AssetDatabase.AssetPathToGUID(
                        destination, AssetPathToGUIDOptions.OnlyExistingAssets))
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
        public IEnumerator Copy_Reconcile_AfterRealDomainReload_ReturnsCompleted()
        {
            CreateTextAsset(ProbeFolder + "/ReloadCopy.txt", "copy reload\n");
            var before = CopyBeforeReload();
            Crumb("copy phase-1 returned ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            // T-W2.4-09 stale-resume self-heal: same bounded re-drive as the
            // move test — a hijacked continuation must pass the this-session
            // probe check or re-drive a real phase-1.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (SentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(SentinelAlive,
                    "the recompile did not produce a domain reload");

                if (ProbeWrittenThisSession("copy") || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupForReDrive();
                CreateTextAsset(ProbeFolder + "/ReloadCopy.txt", "copy reload\n");
                var redrive = CopyBeforeReload();
                Crumb("copy phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = CopyAfterReload();
            Crumb("copy phase-2 returned ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // ── delete reload ──────────────────────────────────────────────

        private static PhaseResult DeleteBeforeReload()
        {
            try
            {
                var path = ProbeFolder + "/ReloadDelete.txt";
                var context = DeleteContext(path);
                var preflight = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(context);
                if (preflight.Diagnostics.Count != 0)
                {
                    return PhaseResult.Fail("preflight diagnostics: " + string.Join("; ",
                        preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
                }
                var executed = new DeleteAssetOperation(() => PermissiveAllowedPaths).Execute(
                    DeleteContext(path, "permanent"));
                if (executed.Kind != StepResultKind.Succeeded)
                {
                    return PhaseResult.Fail("execute failed: " + Describe(executed));
                }
                Directory.CreateDirectory(ProbeStateDir);
                File.WriteAllText(SnapshotStatePath("delete"), preflight.Snapshot.CanonicalText);
                var token = Guid.NewGuid().ToString("N");
                SessionState.SetString(ProbeTokenKey, token);
                File.WriteAllText(SessionStatePath("delete"), token + "\n");
                WriteReloadMarker();
                ArmSentinel();
                return PhaseResult.Pass();
            }
            catch (Exception e)
            {
                return PhaseResult.Fail("phase-1 threw: " + e.Message);
            }
        }

        private static PhaseResult DeleteAfterReload()
        {
            try
            {
                var expectedToken = SessionState.GetString(ProbeTokenKey, "");
                if (!File.Exists(SnapshotStatePath("delete")))
                {
                    return PhaseResult.Fail(
                        expectedToken.Length == 0
                            ? "phase-1 never executed in this Editor session (" +
                                SnapshotStatePath("delete") + " was never written; " +
                                "probeDir exists=" + Directory.Exists(ProbeStateDir) + ")"
                            : "the persisted snapshot " + SnapshotStatePath("delete") +
                                " was deleted after phase-1 wrote it");
                }
                if (expectedToken.Length == 0 || !File.Exists(SessionStatePath("delete")) ||
                    File.ReadAllText(SessionStatePath("delete")).Trim() != expectedToken)
                {
                    return PhaseResult.Fail(
                        "the persisted probe state is stale — a previous Editor " +
                        "session wrote " + SessionStatePath("delete"));
                }
                var parsed = JsonValue.Parse(File.ReadAllText(SnapshotStatePath("delete")));
                if (!parsed.Ok || parsed.Value is null)
                {
                    return PhaseResult.Fail("persisted snapshot did not parse");
                }
                // The recorded input Ref is reused as persisted; the deleted
                // asset cannot be re-resolved after the reload.
                var evidence = (JObject)parsed.Value.ToToken();
                var path = (string)evidence["path"]!;
                var verdict = new DeleteAssetOperation(() => PermissiveAllowedPaths).Reconcile(
                    DeleteContextFromRef(
                        AssetRefJson((string)evidence["guid"]!, path)),
                    parsed.Value);
                if (verdict is not ReconcileResult.Completed)
                {
                    return PhaseResult.Fail("expected Completed, got " + DescribeVerdict(verdict));
                }
                if (File.Exists(PhysicalPath(path)))
                {
                    return PhaseResult.Fail("the deleted artifact reappeared");
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
        public IEnumerator Delete_Reconcile_AfterRealDomainReload_ReturnsCompleted()
        {
            CreateTextAsset(ProbeFolder + "/ReloadDelete.txt", "delete reload\n");
            var before = DeleteBeforeReload();
            Crumb("delete phase-1 returned ok=" + before.Ok + " failure=" + before.Failure);
            Assert.That(before.Ok, Is.True, "pre-reload phase failed: " + before.Failure);

            // T-W2.4-09 stale-resume self-heal: same bounded re-drive as the
            // move test — a hijacked continuation must pass the this-session
            // probe check or re-drive a real phase-1.
            for (var leg = 0; leg <= MaxReDriveLegs; leg++)
            {
                yield return new RecompileScripts();
                var guard = 0;
                while (SentinelAlive && guard++ < 10000)
                {
                    yield return null;
                }
                Assert.IsFalse(SentinelAlive,
                    "the recompile did not produce a domain reload");

                if (ProbeWrittenThisSession("delete") || leg == MaxReDriveLegs)
                {
                    break;
                }
                Crumb("stale-resume re-drive leg=" + (leg + 1));
                CleanupForReDrive();
                CreateTextAsset(ProbeFolder + "/ReloadDelete.txt", "delete reload\n");
                var redrive = DeleteBeforeReload();
                Crumb("delete phase-1 re-drive leg=" + (leg + 1) + " ok=" + redrive.Ok +
                    " failure=" + redrive.Failure);
                Assert.That(redrive.Ok, Is.True,
                    "re-driven pre-reload phase failed: " + redrive.Failure);
            }

            var after = DeleteAfterReload();
            Crumb("delete phase-2 returned ok=" + after.Ok + " failure=" + after.Failure);
            Assert.That(after.Ok, Is.True, "post-reload phase failed: " + after.Failure);
        }

        // ══ fixtures ════════════════════════════════════════════════════

        // A real TextAsset at the path, imported before the step runs —
        // the shared target of all three Operations.
        private static string CreateTextAsset(string path, string contents)
        {
            File.WriteAllText(PhysicalPath(path), contents);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            Assert.That(guid, Is.Not.Empty, "the fixture asset must import: " + path);
            return guid;
        }

        // The §B5 AssetRef document naming the asset at a path.
        private static string AssetRefJson(string path)
        {
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            Assert.That(guid, Is.Not.Empty, "the fixture asset must resolve: " + path);
            return AssetRefJson(guid, path);
        }

        // The §B5 AssetRef document for a recorded guid/path pair —
        // Reconcile contexts must not re-resolve assets the step already
        // moved or deleted.
        private static string AssetRefJson(string guid, string path) =>
            "{\"kind\":\"asset\",\"guid\":\"" + guid + "\",\"path\":\"" + path + "\"}";

        private static UnityStepContext MoveContext(
            string assetPath, string destination, IChangeRecorder? changes = null) =>
            MoveContextFromRef(AssetRefJson(assetPath), destination, changes);

        private static UnityStepContext MoveContextFromRef(
            string assetRef, string destination, IChangeRecorder? changes = null) =>
            Context(
                new Dictionary<string, InputValue>
                {
                    ["asset"] = InputValue.Present(Json(assetRef)),
                    ["destination"] = InputValue.Present(Json("\"" + destination + "\"")),
                },
                MoveInputs, MoveOutputs, changes, null);

        private static UnityStepContext CopyContext(
            string assetPath, string destination, IChangeRecorder? changes = null) =>
            CopyContextFromRef(AssetRefJson(assetPath), destination, changes);

        private static UnityStepContext CopyContextFromRef(
            string assetRef, string destination, IChangeRecorder? changes = null) =>
            Context(
                new Dictionary<string, InputValue>
                {
                    ["asset"] = InputValue.Present(Json(assetRef)),
                    ["destination"] = InputValue.Present(Json("\"" + destination + "\"")),
                },
                MoveInputs, MoveOutputs, changes, null);

        private static UnityStepContext DeleteContext(
            string assetPath, string? mode = null, IChangeRecorder? changes = null) =>
            DeleteContextFromRef(AssetRefJson(assetPath), mode, changes);

        private static UnityStepContext DeleteContextFromRef(
            string assetRef, string? mode = null, IChangeRecorder? changes = null)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["asset"] = InputValue.Present(Json(assetRef)),
            };
            if (mode is not null)
            {
                inputs["mode"] = InputValue.Present(Json("\"" + mode + "\""));
            }
            return Context(inputs, DeleteInputs, NoOutputs, changes, null);
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

        private static JsonValue MoveSnapshot(string source, string destination)
        {
            var preflight = new MoveAssetOperation(() => PermissiveAllowedPaths).Preflight(
                MoveContext(source, destination));
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => "preflight must produce the snapshot: " + string.Join("; ",
                    preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return preflight.Snapshot;
        }

        private static JsonValue CopySnapshot(string source, string destination)
        {
            var preflight = new CopyAssetOperation().Preflight(
                CopyContext(source, destination));
            Assert.That(preflight.Diagnostics, Is.Empty,
                () => "preflight must produce the snapshot: " + string.Join("; ",
                    preflight.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return preflight.Snapshot;
        }

        private static JsonValue DeleteSnapshot(string path)
        {
            var preflight = new DeleteAssetOperation(() => PermissiveAllowedPaths).Preflight(DeleteContext(path));
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

        // Whether the guid still maps to a live asset — the same test
        // Reconcile applies, since GUIDToAssetPath alone can answer with a
        // deleted asset's former path.
        private static bool GuidMapsToLiveAsset(string guid) =>
            AssetArtifact.LiveAssetPath(guid) is not null;

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
