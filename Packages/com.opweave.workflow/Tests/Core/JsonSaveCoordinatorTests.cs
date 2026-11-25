#nullable enable
// T-W5.3-03 [Core] tests (SPEC §H7 item 3): the JsonSaveCoordinator
// per-path OS exclusion — the single write path every OpWeave-controlled
// .opweave.json funnels through — its project-root ownership check
// (junctions and reparse points included) and its crash-recovery intent
// records. The two-process cases drive the OpWeave.LockSmoke helper's
// json-* modes over the product coordinator, exactly like the
// CoordinatorRunLock smokes.
//
// T-W4.6-11 (Issue #400): the lock namespace is the canonical resolved
// target alone — overlapping project roots addressing one physical file
// share one exclusion, distinct files never share, and an unplaceable
// lock still fails closed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Tests
{
    internal sealed class JsonSaveCoordinatorTests
    {
        private readonly List<string> _roots = new List<string>();
        private readonly List<LockSmokeHarness.ChildProcess> _children =
            new List<LockSmokeHarness.ChildProcess>();

        [TearDown]
        public void TearDown()
        {
            LockSmokeHarness.StopAndDisposeAll(_children);
            foreach (var root in _roots)
            {
                LockSmokeHarness.DeleteLockRoot(root);
            }
            _roots.Clear();
        }

        // A scratch project root with the shape a real project has — the
        // target JSON under Assets/.
        private string NewProjectRoot()
        {
            var root = LockSmokeHarness.NewLockRoot();
            _roots.Add(root);
            return root;
        }

        private static string TargetIn(string root, string name) =>
            Path.Combine(root, "Assets", "Workflows", name);

        private static string HashOf(string text) =>
            JsonSaveCoordinator.Sha256Hex(JsonSaveCoordinator.Utf8Bytes(text));

        [Test]
        public void ResolveOwnedPath_InProjectPath_ResolvesInsideRoot()
        {
            var root = NewProjectRoot();
            var coordinator = new JsonSaveCoordinator(root);

            var absolute = coordinator.ResolveOwnedPath(TargetIn(root, "a.opweave.json"));
            Assert.AreEqual(PathResolveStatus.Resolved, absolute.Status);
            Assert.IsTrue(absolute.ResolvedPath!.StartsWith(
                coordinator.ResolvedProjectRoot, StringComparison.OrdinalIgnoreCase));

            var relative = coordinator.ResolveOwnedPath(
                "Assets/Workflows/a.opweave.json");
            Assert.AreEqual(PathResolveStatus.Resolved, relative.Status);
            Assert.AreEqual(
                absolute.ResolvedPath, relative.ResolvedPath,
                "a project-relative path resolves like its absolute form");
        }

        [Test]
        public void ResolveOwnedPath_OutsidePath_Refused()
        {
            var root = NewProjectRoot();
            var coordinator = new JsonSaveCoordinator(root);

            var outside = coordinator.ResolveOwnedPath(
                Path.Combine(Path.GetTempPath(), "outside.opweave.json"));
            Assert.AreEqual(PathResolveStatus.Refused, outside.Status,
                "an absolute path outside the project is refused");

            var escaped = coordinator.ResolveOwnedPath(
                Path.Combine(root, "..", "outside.opweave.json"));
            Assert.AreEqual(PathResolveStatus.Refused, escaped.Status,
                "a '..' escape outside the project is refused");
        }

        [Test]
        public void ResolveOwnedPath_JunctionEscape_Refused()
        {
            var root = NewProjectRoot();
            var outside = LockSmokeHarness.NewLockRoot();
            _roots.Add(outside);
            var link = Path.Combine(root, "linked");
            CreateJunctionOrSymlink(link, outside);

            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(
                Path.Combine(link, "inside", "escape.opweave.json"));
            Assert.AreEqual(PathResolveStatus.Refused, resolved.Status,
                "a path escaping the project through a junction/reparse " +
                "point is refused before a lock is taken");
        }

        [Test]
        public void Acquire_HeldLock_SecondWriterWaitsThenBusy()
        {
            var helper = LockSmokeHarness.BuildHelper();
            var root = NewProjectRoot();
            var target = TargetIn(root, "busy.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;

            var first = coordinator.TryAcquire(resolved);
            Assert.AreEqual(AcquireStatus.Acquired, first.Status);
            var lockPath = first.LockPath;
            using (first.Lock!)
            {
                Assert.IsTrue(File.Exists(lockPath),
                    "the lock file exists beside the target");

                var result = Path.Combine(root, "racer.result");
                var waited = Stopwatch.StartNew();
                var racer = LockSmokeHarness.SpawnHelper(
                    helper,
                    "json-try \"" + root + "\" \"" + target + "\" \"" + result + "\"",
                    null, _children);
                var outcome = LockSmokeHarness.WaitSignalFile(result, racer);
                waited.Stop();
                Assert.AreEqual("busy", outcome.Trim(),
                    "a second process reports Busy when the test host holds the lock");
                Assert.GreaterOrEqual(waited.ElapsedMilliseconds, 1500,
                    "the contended acquisition waited before reporting Busy");
                Assert.IsTrue(File.Exists(lockPath),
                    "the lock file is never deleted while held");
            }

            var third = coordinator.TryAcquire(resolved, 500);
            Assert.AreEqual(AcquireStatus.Acquired, third.Status,
                "the released lock is acquired again");
            third.Lock!.Dispose();
            Assert.IsTrue(File.Exists(lockPath),
                "the lock file is never deleted or replaced");
        }

        [Test]
        public void Acquire_SecondProcessHolds_ThenWaitsThenBusy()
        {
            var helper = LockSmokeHarness.BuildHelper();
            var root = NewProjectRoot();
            var target = TargetIn(root, "two-process.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");

            // First process: holds the target's lock until told to stop.
            var ready = Path.Combine(root, "holder.ready");
            var holder = LockSmokeHarness.SpawnHelper(
                helper,
                "json-hold \"" + root + "\" \"" + target + "\" \"" + ready + "\"",
                ready + ".stop", _children);
            var signal = LockSmokeHarness.WaitSignalFile(ready, holder);
            StringAssert.StartsWith("holding", signal);
            var lockPath = signal.Split('\n')[1].Trim();
            Assert.IsTrue(File.Exists(lockPath),
                "the lock file exists while the second process holds it");

            // Second writer in another process: waits the bounded 2 s, then
            // reports Busy — a live owner's lock is never stolen.
            var result = Path.Combine(root, "racer.result");
            var started = Stopwatch.StartNew();
            var racer = LockSmokeHarness.SpawnHelper(
                helper,
                "json-try \"" + root + "\" \"" + target + "\" \"" + result + "\"",
                null, _children);
            var outcome = LockSmokeHarness.WaitSignalFile(result, racer);
            started.Stop();
            Assert.AreEqual("busy", outcome.Trim(),
                "the contended second process waits, then reports Busy");
            Assert.GreaterOrEqual(started.ElapsedMilliseconds, 1500,
                "the second process waited the bounded window before Busy");
            Assert.IsTrue(File.Exists(lockPath),
                "the held lock file was never deleted or replaced");

            // Orderly release: the lock frees for the next acquirer.
            File.WriteAllText(ready + ".stop", "stop");
            Assert.IsTrue(holder.Process.WaitForExit(15000));
            var after = Path.Combine(root, "after.result");
            var third = LockSmokeHarness.SpawnHelper(
                helper,
                "json-try \"" + root + "\" \"" + target + "\" \"" + after + "\"",
                null, _children);
            Assert.AreEqual("acquired",
                LockSmokeHarness.WaitSignalFile(after, third).Trim(),
                "the lock frees after the holder releases it");
        }

        [Test]
        public void Acquire_KilledProcess_OsReleasesLock()
        {
            var helper = LockSmokeHarness.BuildHelper();
            var root = NewProjectRoot();
            var target = TargetIn(root, "death.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");

            var ready = Path.Combine(root, "doomed.ready");
            var doomed = LockSmokeHarness.SpawnHelper(
                helper,
                "json-hold \"" + root + "\" \"" + target + "\" \"" + ready + "\"",
                null, _children);
            var signal = LockSmokeHarness.WaitSignalFile(ready, doomed);
            StringAssert.StartsWith("holding", signal);
            var lockPath = signal.Split('\n')[1].Trim();
            Assert.IsTrue(File.Exists(lockPath));

            // Abnormal process death: the OS must release the share-mode-0
            // handle — no cleanup ran.
            doomed.Process.Kill();
            Assert.IsTrue(doomed.Process.WaitForExit(15000));
            Assert.IsTrue(File.Exists(lockPath),
                "a dead owner's lock file remains — existence was never the exclusion");

            var after = Path.Combine(root, "survivor.result");
            var survivor = LockSmokeHarness.SpawnHelper(
                helper,
                "json-try \"" + root + "\" \"" + target + "\" \"" + after + "\"",
                null, _children);
            Assert.AreEqual("acquired",
                LockSmokeHarness.WaitSignalFile(after, survivor).Trim(),
                "the OS released the dead process's handle");
        }

        // T-W4.6-11 (Issue #400): a Unity project nested inside another
        // project's directory means two coordinators with different project
        // roots can legitimately resolve one physical file — the lock
        // namespace must be the canonical target alone, so both roots
        // derive the same lock file and the exclusion is mutual.
        [Test]
        public void LockPath_OverlappingRoots_ShareOneNamespace()
        {
            var outer = NewProjectRoot();
            var inner = Path.Combine(outer, "nested", "InnerProject");
            var target = TargetIn(inner, "shared.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");

            var outerCoordinator = new JsonSaveCoordinator(outer);
            var innerCoordinator = new JsonSaveCoordinator(inner);
            var outerResolved = outerCoordinator.ResolveOwnedPath(target);
            var innerResolved = innerCoordinator.ResolveOwnedPath(target);
            Assert.AreEqual(PathResolveStatus.Resolved, outerResolved.Status,
                "the outer root owns the nested file too");
            Assert.AreEqual(PathResolveStatus.Resolved, innerResolved.Status);
            Assert.AreEqual(outerResolved.ResolvedPath, innerResolved.ResolvedPath,
                "both roots resolve one canonical path");

            var outerLockPath = outerCoordinator.LockPathFor(outerResolved.ResolvedPath!);
            var innerLockPath = innerCoordinator.LockPathFor(innerResolved.ResolvedPath!);
            Assert.AreEqual(outerLockPath, innerLockPath,
                "one canonical file means one lock namespace across overlapping roots");
            Assert.AreEqual(
                Path.Combine(Path.GetDirectoryName(outerResolved.ResolvedPath!)!,
                    ".opweave-locks"),
                Path.GetDirectoryName(outerLockPath),
                "the lock file sits beside the canonical target, under no project root");
        }

        [Test]
        [Platform("Win")]
        public void Acquire_OverlappingRoots_MutualExclusion()
        {
            var helper = LockSmokeHarness.BuildHelper();
            var outer = NewProjectRoot();
            var inner = Path.Combine(outer, "nested", "InnerProject");
            var target = TargetIn(inner, "overlap.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(outer);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;

            var held = coordinator.TryAcquire(resolved);
            Assert.AreEqual(AcquireStatus.Acquired, held.Status);
            using (held.Lock!)
            {
                // A second process rooted at the INNER project addresses the
                // same physical file: one lock namespace means it contends,
                // waits the bounded window and reports Busy.
                var result = Path.Combine(outer, "inner-racer.result");
                var waited = Stopwatch.StartNew();
                var racer = LockSmokeHarness.SpawnHelper(
                    helper,
                    "json-try \"" + inner + "\" \"" + target + "\" \"" + result + "\"",
                    null, _children);
                var outcome = LockSmokeHarness.WaitSignalFile(result, racer);
                waited.Stop();
                Assert.AreEqual("busy", outcome.Trim(),
                    "the inner-rooted process shares the held lock's namespace");
                Assert.GreaterOrEqual(waited.ElapsedMilliseconds, 1500,
                    "the contended acquisition waited before reporting Busy");
            }

            // The other direction: a second process rooted at the inner
            // project holds the lock; the outer-rooted coordinator is
            // refused — the exclusion is mutual, not one-sided.
            var ready = Path.Combine(outer, "inner-holder.ready");
            var holder = LockSmokeHarness.SpawnHelper(
                helper,
                "json-hold \"" + inner + "\" \"" + target + "\" \"" + ready + "\"",
                ready + ".stop", _children);
            var signal = LockSmokeHarness.WaitSignalFile(ready, holder);
            StringAssert.StartsWith("holding", signal);
            var busy = coordinator.TryAcquire(resolved, 500);
            Assert.AreEqual(AcquireStatus.Busy, busy.Status,
                "a lock held under the inner root excludes the outer root");
            File.WriteAllText(ready + ".stop", "stop");
            Assert.IsTrue(holder.Process.WaitForExit(15000));

            // Released: the inner-rooted process acquires again.
            var after = Path.Combine(outer, "after.result");
            var third = LockSmokeHarness.SpawnHelper(
                helper,
                "json-try \"" + inner + "\" \"" + target + "\" \"" + after + "\"",
                null, _children);
            Assert.AreEqual("acquired",
                LockSmokeHarness.WaitSignalFile(after, third).Trim(),
                "the lock frees after the holder releases it");
        }

        // Distinct canonical paths never share a lock namespace: a held
        // lock on one file never contends a sibling's acquisition.
        [Test]
        [Platform("Win")]
        public void Acquire_DistinctFiles_NoFalseSharing()
        {
            var root = NewProjectRoot();
            var targetA = TargetIn(root, "first.opweave.json");
            var targetB = TargetIn(root, "second.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(targetA)!);
            File.WriteAllText(targetA, "{}");
            File.WriteAllText(targetB, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolvedA = coordinator.ResolveOwnedPath(targetA).ResolvedPath!;
            var resolvedB = coordinator.ResolveOwnedPath(targetB).ResolvedPath!;

            Assert.AreNotEqual(
                coordinator.LockPathFor(resolvedA),
                coordinator.LockPathFor(resolvedB),
                "distinct files never share a lock namespace");

            var first = coordinator.TryAcquire(resolvedA);
            Assert.AreEqual(AcquireStatus.Acquired, first.Status);
            using (first.Lock!)
            {
                var second = coordinator.TryAcquire(resolvedB, 250);
                Assert.AreEqual(AcquireStatus.Acquired, second.Status,
                    "a held lock on one file never contends a sibling");
                second.Lock!.Dispose();
            }
        }

        // The lock namespace's directory cannot be placed — a plain file
        // already sits where .opweave-locks must be created: acquisition
        // fails closed, never degrading to an unlocked write.
        [Test]
        public void Acquire_LockNamespaceBlocked_FailsClosed()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "blocked.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var lockPath = coordinator.LockPathFor(resolved);

            File.WriteAllText(Path.GetDirectoryName(lockPath)!, "not a directory");
            var result = coordinator.TryAcquire(resolved, 500);
            Assert.AreEqual(AcquireStatus.IoError, result.Status,
                "an unplaceable lock namespace fails closed");
            Assert.IsNull(result.Lock,
                "no exclusion means no write lease");
            Assert.IsFalse(File.Exists(lockPath),
                "no lock file was created");
        }

        // SPEC §E2 item 3 / §H7 item 3 (decision D, T-W4.6-08): the same
        // platform seam forced off Windows makes the JSON save lock
        // unobtainable — the existing failed outcome with the fixed cause,
        // never Busy, never retried — before the lock file is created or
        // opened, and the restored seam takes the Windows path again.
        [Test]
        public void NonWindows_Acquire_FailsWithoutLockFile()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "nonwindows.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var lockPath = coordinator.LockPathFor(resolved);

            var previous = CoordinatorRunLock.IsWindowsPlatform;
            CoordinatorRunLock.IsWindowsPlatform = false;
            try
            {
                var result = coordinator.TryAcquire(resolved, 500);
                Assert.AreEqual(AcquireStatus.IoError, result.Status,
                    "off Windows the lock fails closed — never Busy");
                Assert.AreEqual(
                    "OpWeave v1 supports the Windows Editor only; the cross-process Run exclusion cannot be established on this platform.",
                    result.ErrorMessage);
                Assert.IsFalse(File.Exists(lockPath),
                    "the lock file is neither created nor opened");
            }
            finally
            {
                CoordinatorRunLock.IsWindowsPlatform = previous;
            }

            var after = coordinator.TryAcquire(resolved, 500);
            Assert.AreEqual(AcquireStatus.Acquired, after.Status,
                "the restored Windows path acquires again");
            after.Lock!.Dispose();
        }

        [Test]
        public void Intent_WriteReadClear_RoundTrips()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "intent.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var guid = "aabbccddeeff00112233445566778899";

            var absent = coordinator.ReadIntent(guid, resolved, null);
            Assert.AreEqual(IntentReadState.Absent, absent.State);

            var intent = new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"),
                new string('1', 64), new string('2', 64));
            var written = coordinator.WriteIntent(intent);
            Assert.IsTrue(written.Ok, written.Error);

            var read = coordinator.ReadIntent(guid, resolved, null);
            Assert.AreEqual(IntentReadState.Valid, read.State);
            Assert.AreEqual(resolved, read.Intent!.Path);
            Assert.AreEqual(guid, read.Intent.GraphGuid);
            Assert.AreEqual(JsonSaveCoordinator.IntentKindUpdate, read.Intent.Kind);
            Assert.AreEqual(intent.BaseHash, read.Intent.BaseHash);
            Assert.AreEqual(intent.OutputHash, read.Intent.OutputHash);
            Assert.AreEqual(intent.OutputGuiHash, read.Intent.OutputGuiHash);
            Assert.AreEqual(
                JsonSaveCoordinator.Sha256Hex(
                    File.ReadAllBytes(coordinator.IntentPathFor(guid))),
                read.RecordHash,
                "the read snapshots the record's raw bytes for compare-and-clear");

            var cleared = coordinator.ClearIntent(guid);
            Assert.IsTrue(cleared.Ok, cleared.Error);
            Assert.AreEqual(
                IntentReadState.Absent, coordinator.ReadIntent(guid, resolved, null).State);
        }

        [Test]
        public void Intent_WrongGraphOrPath_Invalid()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "intent-invalid.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var guid = "00112233445566778899aabbccddeeff";

            // A record naming another graph: it is dropped into this
            // graph's record slot (records live under graph-<GUID>), then
            // read — left untouched, never acted on.
            var foreign = new JsonSyncIntent(
                resolved, "ffffffffffffffffffffffffffffffff",
                JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('1', 64), new string('2', 64));
            Assert.IsTrue(coordinator.WriteIntent(foreign).Ok);
            var slot = coordinator.IntentPathFor(guid);
            if (File.Exists(slot))
            {
                File.Delete(slot);
            }
            File.Move(
                coordinator.IntentPathFor("ffffffffffffffffffffffffffffffff"),
                slot);
            var read = coordinator.ReadIntent(guid, resolved, null);
            Assert.AreEqual(IntentReadState.Invalid, read.State);
            Assert.IsTrue(File.Exists(coordinator.IntentPathFor(guid)),
                "an invalid record is left untouched");

            // A record for this graph but a path that is neither the
            // recorded link nor the sibling: invalid too.
            coordinator.ClearIntent(guid);
            var strayed = new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('1', 64), new string('2', 64));
            Assert.IsTrue(coordinator.WriteIntent(strayed).Ok);
            var other = coordinator.ResolveOwnedPath(
                TargetIn(root, "other.opweave.json")).ResolvedPath!;
            var mispointed = coordinator.ReadIntent(guid, other, null);
            Assert.AreEqual(IntentReadState.Invalid, mispointed.State,
                "a record pointing at neither the link nor the sibling is invalid");
        }

        [Test]
        public void Intent_MalformedHashOrKind_Invalid()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "intent-format.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var guid = "0123456789abcdef0123456789abcdef";
            var hex = HashOf("{}");

            // A well-formed update record reads Valid — the baseline every
            // malformed variant below breaks one rule of.
            var wellFormed = new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                hex, new string('1', 64), new string('2', 64));
            Assert.IsTrue(coordinator.WriteIntent(wellFormed).Ok);
            Assert.AreEqual(IntentReadState.Valid,
                coordinator.ReadIntent(guid, resolved, null).State);

            var malformed = new[]
            {
                // Uppercase hex is not the lowercase-64-hex form.
                new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                    hex, new string('A', 64), new string('2', 64)),
                // A shortened hash.
                new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                    hex, "abc123", new string('2', 64)),
                // A non-hex hash.
                new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                    hex, new string('g', 64), new string('2', 64)),
                // An unknown write kind.
                new JsonSyncIntent(
                    resolved, guid, "rename",
                    hex, new string('1', 64), new string('2', 64)),
                // kind/base mismatch: an update bound to "absent".
                new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                    JsonSaveCoordinator.IntentBaseAbsent,
                    new string('1', 64), new string('2', 64)),
                // kind/base mismatch: a create bound to a disk revision.
                new JsonSyncIntent(
                    resolved, guid, JsonSaveCoordinator.IntentKindCreate,
                    hex, new string('1', 64), new string('2', 64)),
            };
            foreach (var record in malformed)
            {
                Assert.IsTrue(coordinator.WriteIntent(record).Ok);
                var read = coordinator.ReadIntent(guid, resolved, null);
                Assert.AreEqual(IntentReadState.Invalid, read.State,
                    "kind=" + record.Kind + " base=" + record.BaseHash +
                    " must read Invalid");
                Assert.IsTrue(File.Exists(coordinator.IntentPathFor(guid)),
                    "an invalid record is left untouched");
            }

            // The allowed combination: a create bound to "absent".
            var create = new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindCreate,
                JsonSaveCoordinator.IntentBaseAbsent,
                new string('1', 64), new string('2', 64));
            Assert.IsTrue(coordinator.WriteIntent(create).Ok);
            Assert.AreEqual(IntentReadState.Valid,
                coordinator.ReadIntent(guid, resolved, null).State,
                "a create bound to absent is the allowed combination");
        }

        [Test]
        public void Intent_SiblingPathForLinkedGraph_Invalid()
        {
            var root = NewProjectRoot();
            var recorded = TargetIn(root, "linked.opweave.json");
            var sibling = TargetIn(root, "sibling.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(recorded)!);
            File.WriteAllText(recorded, "{}");
            File.WriteAllText(sibling, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolvedRecorded = coordinator.ResolveOwnedPath(recorded).ResolvedPath!;
            var resolvedSibling = coordinator.ResolveOwnedPath(sibling).ResolvedPath!;
            var guid = "ffeeddccbbaa00998877665544332211";

            // The leftover record names the deterministic sibling path.
            var record = new JsonSyncIntent(
                resolvedSibling, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('1', 64), new string('2', 64));
            Assert.IsTrue(coordinator.WriteIntent(record).Ok);

            // An unlinked first link (empty recorded path) accepts the
            // sibling — the only case it may.
            Assert.AreEqual(IntentReadState.Valid,
                coordinator.ReadIntent(guid, string.Empty, resolvedSibling).State,
                "an unlinked first link accepts the sibling path");

            // A graph already linked to its JSON accepts only the recorded
            // path: the same record naming the sibling is Invalid.
            Assert.AreEqual(IntentReadState.Invalid,
                coordinator.ReadIntent(guid, resolvedRecorded, resolvedSibling).State,
                "a linked graph never accepts the sibling path");
            Assert.IsTrue(File.Exists(coordinator.IntentPathFor(guid)),
                "an invalid record is left untouched");
        }

        [Test]
        public void Intent_WriteFlushFailure_NotOk()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "intent-flush.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var guid = "102030405060708090a0b0c0d0e0f001";
            var intent = new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('1', 64), new string('2', 64));

            JsonSaveCoordinator.FlushToDiskForTesting =
                _ => throw new IOException("simulated flush failure");
            try
            {
                var written = coordinator.WriteIntent(intent);
                Assert.IsFalse(written.Ok,
                    "a flush failure while writing an intent record is not Ok");
            }
            finally
            {
                JsonSaveCoordinator.FlushToDiskForTesting = null;
            }

            Assert.AreEqual(IntentReadState.Absent,
                coordinator.ReadIntent(guid, resolved, null).State,
                "a failed flush leaves no partial record behind");
            Assert.IsEmpty(
                Directory.GetFiles(coordinator.IntentDirectory, "*.tmp"),
                "the durable temp file is cleaned up");
        }

        [Test]
        public void Intent_ReplaceSharingViolation_RetriesThenFails()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "intent-sharing.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var guid = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
            var intent = new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('1', 64), new string('2', 64));

            Assert.IsTrue(coordinator.WriteIntent(intent).Ok,
                "the baseline record lands before the contended replace");

            // A publish that fails with a sharing violation every time:
            // the same IOException shape DurableRename's MoveFileExW
            // reports for a held destination.
            var attempts = 0;
            JsonSaveCoordinator.PublishReplaceForTesting = (temp, targetPath) =>
            {
                attempts++;
                return new IOException(
                    "simulated sharing violation", unchecked((int)0x80070020));
            };
            try
            {
                var denied = coordinator.WriteIntent(intent);
                Assert.IsFalse(denied.Ok,
                    "a sharing violation on replace fails closed");
                // The retry bound is the Windows contract; elsewhere the
                // classification never applies and the failure is final.
                Assert.AreEqual(
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                        ? JsonSaveCoordinator.MaxPublishAttempts
                        : 1,
                    attempts, "the replace is retried a bounded number of times");
            }
            finally
            {
                JsonSaveCoordinator.PublishReplaceForTesting = null;
            }
            Assert.AreEqual(IntentReadState.Valid,
                coordinator.ReadIntent(guid, resolved, null).State,
                "the previous record survives a failed replace untouched");
            Assert.IsEmpty(
                Directory.GetFiles(coordinator.IntentDirectory, "*.tmp"),
                "the durable temp file is cleaned up");

            // The real-FS variant: a reader holding the record without
            // share-delete makes the actual durable rename fail; the write
            // fails closed and the held record stays.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var intentPath = coordinator.IntentPathFor(guid);
                using (new FileStream(
                    intentPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var denied = coordinator.WriteIntent(intent);
                    Assert.IsFalse(denied.Ok,
                        "a held record makes the real publish fail closed");
                }
                Assert.AreEqual(IntentReadState.Valid,
                    coordinator.ReadIntent(guid, resolved, null).State,
                    "the held record is left untouched");
            }
        }

        [Test]
        [Platform("Win")]
        public void Intent_ReplaceHeldTarget_ReleasedDuringRetry_Succeeds()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "intent-held-target.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var guid = "5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d";
            Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('1', 64), new string('2', 64))).Ok);

            var intentPath = coordinator.IntentPathFor(guid);
            using var holder = new FileStream(
                intentPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var attempts = 0;
            JsonSaveCoordinator.PublishReplaceForTesting = (temp, targetPath) =>
            {
                attempts++;
                try
                {
                    DurableRename.Move(temp, targetPath);
                    return null;
                }
                catch (IOException e)
                {
                    // Release only after the first real move has failed on the held file.
                    if (attempts == 1)
                    {
                        holder.Dispose();
                    }
                    return e;
                }
            };
            var updated = new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('5', 64), new string('6', 64));
            try
            {
                var written = coordinator.WriteIntent(updated);
                Assert.IsTrue(written.Ok,
                    "the replace lands once the reader releases inside " +
                    "the bounded retry window");
            }
            finally
            {
                JsonSaveCoordinator.PublishReplaceForTesting = null;
            }
            Assert.AreEqual(2, attempts,
                "the held record made the first real publish fail; the next attempt succeeded");

            var read = coordinator.ReadIntent(guid, resolved, null);
            Assert.AreEqual(IntentReadState.Valid, read.State);
            Assert.AreEqual(updated.OutputHash, read.Intent!.OutputHash,
                "the retried publish landed the newer record");
            Assert.IsEmpty(
                Directory.GetFiles(coordinator.IntentDirectory, "*.tmp"),
                "the durable temp file is cleaned up");
        }

        [Test]
        public void Intent_MalformedGraphGuid_Rejected()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "intent-guid.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var guid = "00112233445566778899aabbccddeeff";

            // Only exactly-32-lowercase-hex asset GUIDs may name an intent
            // record: a separator or ".." must never escape the jsonsync
            // directory.
            var malformed = new[]
            {
                string.Empty,                                   // empty
                "00112233",                                     // short
                "00112233445566778899aabbccddeeff00",           // long
                "00112233445566778899AABBCCDDEEFF",             // uppercase
                "00112233445566778899aabbccddeefg",             // non-hex
                "../00112233445566778899aabbccddee",            // ".." + '/' — 32 chars
                "..\\..\\00112233445566778899aabbcc",           // ".." + '\' — 32 chars
            };
            foreach (var bad in malformed)
            {
                Assert.AreEqual(IntentReadState.Invalid,
                    coordinator.ReadIntent(bad, resolved, null).State,
                    "ReadIntent rejects '" + bad + "'");
                Assert.IsFalse(coordinator.WriteIntent(new JsonSyncIntent(
                    resolved, bad, JsonSaveCoordinator.IntentKindUpdate,
                    HashOf("{}"), new string('1', 64), new string('2', 64))).Ok,
                    "WriteIntent rejects '" + bad + "'");
                Assert.IsFalse(coordinator.ClearIntent(bad).Ok,
                    "ClearIntent rejects '" + bad + "'");
                var cleared = coordinator.ClearIntentIfUnchanged(
                    bad, new string('0', 64));
                Assert.IsFalse(cleared.Ok,
                    "ClearIntentIfUnchanged rejects '" + bad + "'");
                Assert.IsFalse(cleared.Cleared);
            }

            // None of the rejections touched the file system — even the
            // intent directory itself was never created.
            Assert.IsFalse(
                Directory.Exists(Path.Combine(root, "Library", "OpWeave")),
                "a malformed GUID never reaches the file system");

            // And the same rejections leave a real record untouched.
            Assert.IsTrue(coordinator.WriteIntent(new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('1', 64), new string('2', 64))).Ok,
                "a real record to prove the rejections leave it untouched");
            foreach (var bad in malformed)
            {
                Assert.IsFalse(coordinator.ClearIntent(bad).Ok);
                Assert.IsFalse(
                    coordinator.ClearIntentIfUnchanged(bad, new string('0', 64)).Ok);
            }
            Assert.AreEqual(IntentReadState.Valid,
                coordinator.ReadIntent(guid, resolved, null).State,
                "the valid record is untouched");
        }

        [Test]
        public void Intent_ClearIfUnchanged_KeepsNewerRecord()
        {
            var root = NewProjectRoot();
            var target = TargetIn(root, "intent-compare.opweave.json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var coordinator = new JsonSaveCoordinator(root);
            var resolved = coordinator.ResolveOwnedPath(target).ResolvedPath!;
            var guid = "abcdef0123456789abcdef0123456789";
            var first = new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('1', 64), new string('2', 64));
            Assert.IsTrue(coordinator.WriteIntent(first).Ok);

            // The caller's snapshot of the record as read.
            var snapshot = coordinator.ReadIntent(guid, resolved, null).RecordHash!;

            // A newer record lands after the snapshot: compare-and-clear
            // must keep it.
            var newer = new JsonSyncIntent(
                resolved, guid, JsonSaveCoordinator.IntentKindUpdate,
                HashOf("{}"), new string('3', 64), new string('4', 64));
            Assert.IsTrue(coordinator.WriteIntent(newer).Ok);
            var kept = coordinator.ClearIntentIfUnchanged(guid, snapshot);
            Assert.IsTrue(kept.Ok, kept.Error);
            Assert.IsFalse(kept.Cleared,
                "a record that changed after the snapshot is never cleared");
            var still = coordinator.ReadIntent(guid, resolved, null);
            Assert.AreEqual(IntentReadState.Valid, still.State);
            Assert.AreEqual(newer.OutputHash, still.Intent!.OutputHash,
                "the newer record survived the compare-and-clear");

            // A current snapshot clears.
            var cleared = coordinator.ClearIntentIfUnchanged(guid, still.RecordHash!);
            Assert.IsTrue(cleared.Ok, cleared.Error);
            Assert.IsTrue(cleared.Cleared,
                "an unchanged record clears");
            Assert.AreEqual(IntentReadState.Absent,
                coordinator.ReadIntent(guid, resolved, null).State);
        }

        // A junction (Windows) or directory symlink (Unix) at link pointing
        // at target — the reparse-point escape the ownership check must
        // resolve.
        private static void CreateJunctionOrSymlink(string link, string target)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var psi = new ProcessStartInfo(
                    "cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var process = Process.Start(psi)!;
                process.WaitForExit(15000);
                Assert.AreEqual(0, process.ExitCode,
                    "mklink /J failed: " + process.StandardOutput.ReadToEnd() +
                    process.StandardError.ReadToEnd());
                process.Dispose();
            }
            else
            {
                var psi = new ProcessStartInfo(
                    "ln", "-s \"" + target + "\" \"" + link + "\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var process = Process.Start(psi)!;
                process.WaitForExit(15000);
                Assert.AreEqual(0, process.ExitCode,
                    "ln -s failed: " + process.StandardOutput.ReadToEnd() +
                    process.StandardError.ReadToEnd());
                process.Dispose();
            }
        }
    }
}
