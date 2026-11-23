#nullable enable

using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the coordinator.lock / lock.json pair of SPEC §E2
    /// item 3 and §F3 item 2 (local:T-W1.7-04:lockjson-critical-section):
    /// lock.json holds exactly runId/editorSessionId/acquiredAt while the OS
    /// exclusion is held, every read/write happens inside the held critical
    /// section (asserted through the internal probe seam that itself
    /// re-verifies the OS exclusion at each mutation point), release empties
    /// lock.json before dropping the handle, a second acquisition in the
    /// same process fails with OW-RUN-002, coordinator.lock is never
    /// replaced or deleted, and the abandoned-state semantics of Dispose.
    /// The same sources run under dotnet and Unity Mono.
    /// </summary>
    internal sealed class CoordinatorRunLockTests
    {
        private const string Session = "session-lock-test";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        private string _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(), "OpWeave.RunLock." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private string LockPath => Path.Combine(_root, CoordinatorRunLock.LockFileName);
        private string StatePath => Path.Combine(_root, CoordinatorRunLock.StateFileName);

        private static CoordinatorRunLock Acquire(string root)
        {
            var acquired = CoordinatorRunLock.TryAcquire(root);
            Assert.That(acquired.Ok, Is.True,
                () => "TryAcquire(" + root + ") failed: " + DescribeDiagnostics(acquired));
            return acquired.Value!;
        }

        // A failure message that can never itself throw: every diagnostic
        // code and message joined; "<none>" marks a diagnostic-free
        // failure instead of indexing past the list.
        private static string DescribeDiagnostics<T>(Result<T> result)
        {
            if (result.Diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            var parts = new string[result.Diagnostics.Count];
            for (var i = 0; i < parts.Length; i++)
            {
                var diagnostic = result.Diagnostics[i];
                parts[i] = diagnostic is null
                    ? "<null diagnostic>"
                    : diagnostic.Code + " " + diagnostic.Message;
            }
            return string.Join("; ", parts);
        }

        // Raw OS-level open attempt on coordinator.lock — outside the
        // product API, the same denied open a foreign process sees.
        private static bool OsOpenDenied(string lockPath)
        {
            try
            {
                using (new FileStream(
                    lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return false;
                }
            }
            catch (Exception e) when (
                e is IOException || e is UnauthorizedAccessException)
            {
                return true;
            }
        }

        // ── lock.json lifecycle (SPEC §E2 item 3) ──────────────────────────

        // While the exclusion is held, lock.json carries exactly the three
        // SPEC members; after the orderly release it is empty.
        [Test]
        public void LockJsonHoldsExactlyThreeMembersWhileHeld_AndIsEmptyAfterRelease()
        {
            var runId = Identifiers.NewRunId();
            var sessionId = Guid.NewGuid().ToString("N");

            var held = Acquire(_root);
            var acquired = RunLockState.AcquiredNow(runId, sessionId);
            Assert.IsTrue(held.WriteState(acquired).Ok);

            var text = File.ReadAllText(StatePath, s_utf8);
            var document = (JObject)CanonicalJson.Parse(text).Value!;
            Assert.AreEqual(3, document.Count, "lock.json holds exactly the SPEC members");
            Assert.AreEqual(runId, (string?)document["runId"]);
            Assert.AreEqual(sessionId, (string?)document["editorSessionId"]);
            var acquiredAt = (string?)document["acquiredAt"];
            Assert.IsNotNull(acquiredAt);
            Assert.AreEqual(acquired.AcquiredAt, acquiredAt);
            StringAssert.IsMatch(
                @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", acquiredAt!,
                "acquiredAt is the UTC acquisition timestamp");

            var read = held.ReadState();
            Assert.IsTrue(read.Ok);
            Assert.AreEqual(acquired, read.Value);

            Assert.IsTrue(held.Release().Ok);
            Assert.AreEqual(0, new FileInfo(StatePath).Length, "lock.json is emptied before release");
        }

        // SPEC §E2 item 3 / §F3 item 2: every lock.json access happens inside
        // the held OS critical section — the probe re-derives the exclusion
        // at each mutation point — and the emptying write lands strictly
        // before the OS handle drops.
        [Test]
        public void EveryLockJsonAccessHappensInsideTheHeldCriticalSection()
        {
            var held = Acquire(_root);
            var deniedInside = 0;
            held.CriticalSectionProbe = stage =>
            {
                Assert.IsTrue(OsOpenDenied(LockPath),
                    "coordinator.lock must still be OS-exclusive at the " + stage + " point");
                deniedInside++;
            };

            Assert.IsTrue(held.WriteState(RunLockState.AcquiredNow(Identifiers.NewRunId(), Session)).Ok);
            Assert.IsTrue(held.ReadState().Ok);
            var releases = deniedInside;
            Assert.IsTrue(held.Release().Ok);
            Assert.AreEqual(3, deniedInside,
                "read, write and release each ran inside the held critical section");
            Assert.GreaterOrEqual(releases, 2, "the write and read points were observed");
            Assert.IsFalse(OsOpenDenied(LockPath),
                "the OS exclusion must be released with the handle");
            Assert.AreEqual(0, new FileInfo(StatePath).Length);
        }

        // SPEC §E2: lock.json must be emptied BEFORE the exclusion is
        // released — a failed emptying keeps the OS lock held so the
        // caller can retry Release. Occupying lock.json's path with a
        // directory makes the atomic temp-file replace fail on every
        // platform (a file cannot be moved over a directory); removing
        // the fault lets the retry empty the file and drop the handle.
        [Test]
        public void FailedEmptyingKeepsTheOsLockHeldUntilRetry()
        {
            var held = Acquire(_root);
            Assert.IsTrue(held.WriteState(
                RunLockState.AcquiredNow(Identifiers.NewRunId(), Session)).Ok);

            File.Delete(StatePath);
            Directory.CreateDirectory(StatePath);
            try
            {
                var released = held.Release();
                Assert.IsFalse(released.Ok);
                Assert.AreEqual("OW-STORE-001", released.Diagnostics[0].Code);
                Assert.IsTrue(held.IsHeld,
                    "a failed emptying must keep the OS lock held");
                Assert.IsTrue(OsOpenDenied(LockPath),
                    "the exclusion stays held for the retry");
                Assert.IsTrue(held.ReadState().Ok,
                    "the still-held critical section still guards lock.json reads");
            }
            finally
            {
                Directory.Delete(StatePath);
            }

            var retried = held.Release();
            Assert.IsTrue(retried.Ok,
                "Release must succeed once the emptying fault is removed");
            Assert.IsFalse(held.IsHeld);
            Assert.IsFalse(OsOpenDenied(LockPath),
                "the retry released the OS exclusion");
            Assert.AreEqual(0, new FileInfo(StatePath).Length,
                "the retry emptied lock.json before releasing");
        }

        // SPEC §E2 item 3: lock.json reads null while nothing is recorded —
        // missing file and the zero-length released form alike.
        [Test]
        public void ReadStateReportsNullForMissingOrEmptyLockJson()
        {
            var held = Acquire(_root);
            var missing = held.ReadState();
            Assert.IsTrue(missing.Ok);
            Assert.IsNull(missing.Value);

            File.WriteAllText(StatePath, "", s_utf8);
            var empty = held.ReadState();
            Assert.IsTrue(empty.Ok);
            Assert.IsNull(empty.Value);
            held.Dispose();
        }

        // A present-but-unreadable lock.json is not silently adopted: the
        // reader reports OW-STORE-002 and the caller decides (NeedsReview
        // belongs to recovery, T-W4.3-01).
        [Test]
        public void CorruptLockJsonReportsOwStore002()
        {
            File.WriteAllText(StatePath, "not a lock state", s_utf8);
            var held = Acquire(_root);
            var read = held.ReadState();
            Assert.IsFalse(read.Ok);
            Assert.AreEqual("OW-STORE-002", read.Diagnostics[0].Code);
            held.Dispose();

            File.WriteAllText(StatePath, "{}", s_utf8);
            held = Acquire(_root);
            read = held.ReadState();
            Assert.IsFalse(read.Ok, "an object without the SPEC members is not a recorded state");
            Assert.AreEqual("OW-STORE-002", read.Diagnostics[0].Code);
            held.Dispose();
        }

        // Bytes that are not valid UTF-8 never even decode into JSON text:
        // the strict decoder's DecoderFallbackException must surface as the
        // same corrupt recorded state — OW-STORE-002, never an escaping
        // exception (0xC3 starts a two-byte sequence, 0x28 is not a valid
        // continuation byte).
        [Test]
        public void InvalidUtf8LockJsonReportsOwStore002()
        {
            File.WriteAllBytes(StatePath, new byte[] { 0xC3, 0x28 });
            var held = Acquire(_root);
            try
            {
                var read = held.ReadState();
                Assert.IsFalse(read.Ok,
                    "undecodable lock.json bytes are not a readable lock state");
                Assert.AreEqual("OW-STORE-002", read.Diagnostics[0].Code);
            }
            finally
            {
                held.Dispose();
            }
        }

        // A required member holding a non-string token — object or array —
        // is corruption, not a conversion exception: every combination
        // reports OW-STORE-002 instead of throwing on JToken.Value<string>().
        [Test]
        public void NonStringLockJsonMembersReportOwStore002()
        {
            var state = RunLockState.AcquiredNow(Identifiers.NewRunId(), Session);
            var held = Acquire(_root);
            try
            {
                foreach (var member in new[]
                {
                    "runId", "editorSessionId", "acquiredAt",
                })
                {
                    foreach (var token in new JToken[] { new JObject(), new JArray() })
                    {
                        var document = new JObject
                        {
                            ["runId"] = state.RunId,
                            ["editorSessionId"] = state.EditorSessionId,
                            ["acquiredAt"] = state.AcquiredAt,
                        };
                        document[member] = token;
                        File.WriteAllText(
                            StatePath, CanonicalJson.Serialize(document), s_utf8);
                        var read = held.ReadState();
                        Assert.IsFalse(read.Ok,
                            member + " = " + token.Type +
                                " is not a readable lock state member");
                        Assert.AreEqual("OW-STORE-002", read.Diagnostics[0].Code);
                    }
                }
            }
            finally
            {
                held.Dispose();
            }
        }

        // ── exclusion semantics ───────────────────────────────────────────

        // SPEC §E2 item 3: a second acquisition — same process family or a
        // foreign process — fails with OW-RUN-002 while the owner holds, and
        // the owner's lock is undisturbed.
        [Test]
        public void SecondAcquisitionFailsWithOwRun002WhileHeld()
        {
            var held = Acquire(_root);
            var second = CoordinatorRunLock.TryAcquire(_root);
            Assert.IsFalse(second.Ok);
            Assert.AreEqual("OW-RUN-002", second.Diagnostics[0].Code);
            Assert.IsTrue(OsOpenDenied(LockPath), "the raw open must be denied while held");

            Assert.IsTrue(held.IsHeld);
            Assert.IsTrue(held.WriteState(RunLockState.AcquiredNow(Identifiers.NewRunId(), Session)).Ok);
            Assert.IsTrue(held.Release().Ok);

            var after = CoordinatorRunLock.TryAcquire(_root);
            Assert.IsTrue(after.Ok, "the released exclusion is acquirable again");
            after.Value!.Dispose();
        }

        // SPEC §E2 item 3 "v1 の対象外の OS" (decision D, T-W4.6-08): with
        // the platform seam forced off Windows the exclusion is treated as
        // unobtainable — OW-RUN-002 with the fixed cause — BEFORE
        // coordinator.lock is opened or created, and the restored seam takes
        // the Windows path again.
        [Test]
        public void NonWindows_TryAcquire_FailsClosedWithoutOpeningTheLock()
        {
            var previous = CoordinatorRunLock.IsWindowsPlatform;
            CoordinatorRunLock.IsWindowsPlatform = false;
            try
            {
                var acquired = CoordinatorRunLock.TryAcquire(_root);
                Assert.IsFalse(acquired.Ok,
                    "off Windows the exclusion is unobtainable, like a live owner's");
                Assert.AreEqual("OW-RUN-002", acquired.Diagnostics[0].Code);
                Assert.AreEqual(
                    "OpWeave v1 supports the Windows Editor only; the cross-process Run exclusion cannot be established on this platform.",
                    acquired.Diagnostics[0].Message);
                Assert.IsFalse(File.Exists(LockPath),
                    "coordinator.lock is neither created nor opened");
            }
            finally
            {
                CoordinatorRunLock.IsWindowsPlatform = previous;
            }

            var windows = CoordinatorRunLock.TryAcquire(_root);
            Assert.That(windows.Ok, Is.True,
                () => "the restored Windows path acquires again: " +
                    DescribeDiagnostics(windows));
            windows.Value!.Dispose();
        }

        // SPEC §E2/§F3: coordinator.lock is created once and never replaced
        // or deleted — not by writes, not by release, not by re-acquisition.
        [Test]
        public void CoordinatorLockFileIsNeverReplacedOrDeleted()
        {
            var held = Acquire(_root);
            Assert.IsTrue(File.Exists(LockPath));
            var created = File.GetCreationTimeUtc(LockPath);
            var written = File.GetLastWriteTimeUtc(LockPath);

            Assert.IsTrue(held.WriteState(RunLockState.AcquiredNow(Identifiers.NewRunId(), Session)).Ok);
            Assert.IsTrue(held.Release().Ok);
            Assert.IsTrue(File.Exists(LockPath), "the lock file is not deleted after release");

            var held2 = Acquire(_root);
            held2.Dispose();

            Assert.AreEqual(created, File.GetCreationTimeUtc(LockPath),
                "the same file object must persist — a replace would recreate it");
            Assert.AreEqual(written, File.GetLastWriteTimeUtc(LockPath),
                "the lock file content is never written");
            Assert.AreEqual(
                0, Directory.GetFiles(_root, "*" + ".opweave-tmp").Length,
                "no temp file remains");
        }

        // Dispose is the abandoned-state path: the OS exclusion drops but
        // the recorded state survives for recovery — exactly what an
        // abnormal termination leaves (SPEC §E2, §F4).
        [Test]
        public void DisposeDropsTheOsLockButKeepsTheRecordedState()
        {
            var runId = Identifiers.NewRunId();
            var held = Acquire(_root);
            var state = RunLockState.AcquiredNow(runId, Session);
            Assert.IsTrue(held.WriteState(state).Ok);
            held.Dispose();

            Assert.IsFalse(OsOpenDenied(LockPath), "Dispose releases the OS exclusion");
            var recovered = Acquire(_root);
            var read = recovered.ReadState();
            Assert.IsTrue(read.Ok);
            Assert.AreEqual(state, read.Value,
                "the recorded owner state must survive an unordered teardown");
            recovered.Dispose();
        }

        // The OS-handle adoption seam of SPEC §E2: a persisted handle value
        // re-attaches as a working exclusion — the domain-reload mechanism
        // the [Edit] observation exercises in a real Unity Editor.
        [Test]
        public void TryAdoptReattachesASurvivingHandle()
        {
            var runId = Identifiers.NewRunId();
            var held = Acquire(_root);
            var state = RunLockState.AcquiredNow(runId, Session);
            Assert.IsTrue(held.WriteState(state).Ok);
            var handleValue = held.LockHandleValue;
            Assert.AreNotEqual(IntPtr.Zero, handleValue);
            // The previous domain is gone: its managed object is abandoned —
            // on Windows the raw OS handle has no managed owner at all.
            held = null!;

            var adopted = CoordinatorRunLock.TryAdopt(_root, handleValue);
            Assert.That(adopted.Ok, Is.True,
                () => "TryAdopt(" + _root + ", 0x" + handleValue.ToString("x") +
                    ") failed: " + DescribeDiagnostics(adopted));
            var reattached = adopted.Value!;
            Assert.IsTrue(reattached.IsHeld);
            Assert.IsTrue(OsOpenDenied(LockPath), "the adopted handle still excludes");
            var read = reattached.ReadState();
            Assert.IsTrue(read.Ok);
            Assert.AreEqual(state, read.Value);

            Assert.IsTrue(reattached.Release().Ok);
            Assert.IsFalse(OsOpenDenied(LockPath));

            var reacquired = CoordinatorRunLock.TryAcquire(_root);
            Assert.That(reacquired.Ok, Is.True,
                "a fresh acquisition succeeds after the adopted handle was released");
            reacquired.Value!.Dispose();
        }

        // SPEC §E2 adoption proof (ii): a valid live file handle that does
        // not resolve to this root's coordinator.lock is rejected — the
        // value alone is never proof — and a foreign handle is left
        // untouched by the rejected adoption.
        [Test]
        public void TryAdoptRejectsAHandleToAnUnrelatedFile()
        {
            var held = Acquire(_root);
            try
            {
                var unrelatedPath = Path.Combine(_root, "unrelated.txt");
                File.WriteAllBytes(unrelatedPath, new byte[] { 0x42 });
                using (var foreign = new FileStream(
                    unrelatedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var adopted = CoordinatorRunLock.TryAdopt(
                        _root, foreign.SafeFileHandle.DangerousGetHandle());
                    Assert.IsFalse(adopted.Ok);
                    Assert.AreEqual("OW-STORE-001", adopted.Diagnostics[0].Code);
                    Assert.IsFalse(foreign.SafeFileHandle.IsClosed,
                        "a rejected adoption must not close a foreign handle");
                    Assert.IsTrue(
                        OsOpenDenied(unrelatedPath),
                        "the foreign handle's own exclusion must be undisturbed");
                }
            }
            finally
            {
                held.Dispose();
            }
        }

        // SPEC §E2 adoption proof (ii): the same file name under a
        // different lock root is a different exclusion — a handle to root
        // B's coordinator.lock is not proof of holding root A's, and the
        // rejection leaves the other root's live lock alone.
        [Test]
        public void TryAdoptRejectsAHandleToADifferentLockRoot()
        {
            var held = Acquire(_root);
            var otherRoot = Path.Combine(
                Path.GetTempPath(), "OpWeave.RunLock.Other." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherRoot);
            try
            {
                var otherHeld = Acquire(otherRoot);
                try
                {
                    var adopted = CoordinatorRunLock.TryAdopt(_root, otherHeld.LockHandleValue);
                    Assert.IsFalse(adopted.Ok);
                    Assert.AreEqual("OW-STORE-001", adopted.Diagnostics[0].Code);
                    Assert.IsTrue(otherHeld.IsHeld,
                        "rejection must not disturb the other root's live lock");
                    Assert.IsTrue(OsOpenDenied(
                        Path.Combine(otherRoot, CoordinatorRunLock.LockFileName)));
                }
                finally
                {
                    otherHeld.Dispose();
                }
            }
            finally
            {
                held.Dispose();
                Directory.Delete(otherRoot, recursive: true);
            }
        }

        // SPEC §E2 adoption proof (iii): the identity match alone is not
        // ownership — a same-path handle opened GENERIC_READ with
        // FILE_SHARE_READ resolves to this root's coordinator.lock yet
        // holds no exclusion: its share mode denies write probes while
        // still permitting readers, so a single denied probe must not be
        // misread as an exclusive holder. Adoption rejects it, while the
        // genuine share-mode-0 handle is adopted.
        [Test]
        public void TryAdoptRequiresTheProvenExclusiveHolder()
        {
            File.WriteAllBytes(LockPath, Array.Empty<byte>());
            using (var shared = new FileStream(
                LockPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var rejected = CoordinatorRunLock.TryAdopt(
                    _root, shared.SafeFileHandle.DangerousGetHandle());
                Assert.IsFalse(rejected.Ok,
                    "a same-path non-exclusive handle is not the lock owner");
                Assert.AreEqual("OW-STORE-001", rejected.Diagnostics[0].Code);
                Assert.IsFalse(shared.SafeFileHandle.IsClosed,
                    "a rejected adoption must not close the foreign handle");
            }

            var held = Acquire(_root);
            var handleValue = held.LockHandleValue;
            // The previous domain is gone: its managed object is
            // abandoned — on Windows the raw OS handle has no managed
            // owner at all.
            held = null!;
            var adopted = CoordinatorRunLock.TryAdopt(_root, handleValue);
            Assert.That(adopted.Ok, Is.True,
                () => "the genuine exclusive handle must adopt: " +
                    DescribeDiagnostics(adopted));
            Assert.IsTrue(adopted.Value!.IsHeld);
            Assert.IsTrue(adopted.Value!.Release().Ok);
        }

        // SPEC §E2 adoption proof (i): a stale value — once a real handle,
        // now closed — resolves to nothing through the OS and is rejected.
        [Test]
        public void TryAdoptRejectsAClosedHandleValue()
        {
            var held = Acquire(_root);
            try
            {
                IntPtr stale;
                using (var temp = new FileStream(
                    Path.Combine(_root, "stale.tmp"),
                    FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                {
                    stale = temp.SafeFileHandle.DangerousGetHandle();
                }
                var adopted = CoordinatorRunLock.TryAdopt(_root, stale);
                Assert.IsFalse(adopted.Ok);
                Assert.AreEqual("OW-STORE-001", adopted.Diagnostics[0].Code);
            }
            finally
            {
                held.Dispose();
            }
        }

        // A dead or bogus handle is the SPEC §E2 "could not be kept" case:
        // adoption fails closed instead of fabricating an exclusion.
        [Test]
        public void TryAdoptRejectsADeadHandle()
        {
            Acquire(_root).Dispose();
            var adopted = CoordinatorRunLock.TryAdopt(_root, new IntPtr(0x2BAD));
            Assert.IsFalse(adopted.Ok);
            Assert.AreEqual("OW-STORE-001", adopted.Diagnostics[0].Code);

            var zero = CoordinatorRunLock.TryAdopt(_root, IntPtr.Zero);
            Assert.IsFalse(zero.Ok);
        }

        // The SPEC §E2 fail-closed contract is never silent: every TryAdopt
        // rejection branch — empty value, missing lock file, unresolvable or
        // non-exclusive handle, and the non-Windows platform rejection —
        // returns at least one diagnostic with a published code and a fixed
        // English message, so a lost exclusion is always explainable.
        [Test]
        public void EveryTryAdoptFailureBranchCarriesADiagnostic()
        {
            var held = Acquire(_root);
            try
            {
                // Empty and unresolvable candidate values.
                AssertFailedAdoption(CoordinatorRunLock.TryAdopt(_root, IntPtr.Zero));
                AssertFailedAdoption(CoordinatorRunLock.TryAdopt(_root, new IntPtr(-1)));
                AssertFailedAdoption(CoordinatorRunLock.TryAdopt(_root, new IntPtr(0x2BAD)));

                // A root whose coordinator.lock does not exist.
                var emptyRoot = Path.Combine(_root, "empty-root");
                Directory.CreateDirectory(emptyRoot);
                AssertFailedAdoption(
                    CoordinatorRunLock.TryAdopt(emptyRoot, new IntPtr(0x1234)));

                // A live handle that does not resolve to this root's
                // coordinator.lock.
                var foreignPath = Path.Combine(_root, "foreign.bin");
                File.WriteAllBytes(foreignPath, new byte[] { 0x42 });
                using (var foreign = new FileStream(
                    foreignPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    AssertFailedAdoption(CoordinatorRunLock.TryAdopt(
                        _root, foreign.SafeFileHandle.DangerousGetHandle()));
                }

                // A live handle resolving to a coordinator.lock that is not
                // exclusively held — the holder-proof step must deny it.
                var sharedRoot = Path.Combine(_root, "shared-root");
                Directory.CreateDirectory(sharedRoot);
                var sharedLockPath = Path.Combine(
                    sharedRoot, CoordinatorRunLock.LockFileName);
                File.WriteAllBytes(sharedLockPath, Array.Empty<byte>());
                using (var shared = new FileStream(
                    sharedLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    AssertFailedAdoption(CoordinatorRunLock.TryAdopt(
                        sharedRoot, shared.SafeFileHandle.DangerousGetHandle()));
                }
            }
            finally
            {
                held.Dispose();
            }
        }

        private static void AssertFailedAdoption(Result<CoordinatorRunLock> adopted)
        {
            Assert.IsFalse(adopted.Ok);
            Assert.That(adopted.Diagnostics, Is.Not.Empty,
                "a failed adoption must carry at least one diagnostic");
            Assert.IsFalse(string.IsNullOrEmpty(adopted.Diagnostics[0].Code),
                "the diagnostic must carry a published code");
            Assert.IsFalse(string.IsNullOrEmpty(adopted.Diagnostics[0].Message),
                "the diagnostic must carry a fixed English message");
        }

        // ── boundary conditions ───────────────────────────────────────────

        // The critical-section discipline is enforced on every state
        // operation: on a released or disposed object they are programming
        // errors, never silent reads or writes outside the OS exclusion.
        [Test]
        public void StateOperationsRequireAHeldLock()
        {
            var state = RunLockState.AcquiredNow(Identifiers.NewRunId(), Session);
            var held = Acquire(_root);
            Assert.IsTrue(held.Release().Ok);
            Assert.Throws<InvalidOperationException>(() => held.ReadState());
            Assert.Throws<InvalidOperationException>(() => held.WriteState(state));
            Assert.Throws<InvalidOperationException>(() => held.Release());

            var disposed = Acquire(_root);
            disposed.Dispose();
            Assert.Throws<ObjectDisposedException>(() => disposed.ReadState());
            Assert.Throws<ObjectDisposedException>(() => disposed.WriteState(state));
        }

        // Acquisition arguments are validated like FileRunStore's.
        [Test]
        public void TryAcquireRejectsAnEmptyRoot()
        {
            Assert.Throws<ArgumentException>(() => CoordinatorRunLock.TryAcquire(""));
            Assert.Throws<ArgumentException>(() => CoordinatorRunLock.TryAcquire(null!));
            Assert.Throws<ArgumentException>(() => CoordinatorRunLock.TryAdopt("", IntPtr.Zero));
        }

        // The atomic temp replace leaves nothing behind (SPEC §F3 item 2).
        [Test]
        public void AtomicReplaceLeavesNoTempFiles()
        {
            var held = Acquire(_root);
            Assert.IsTrue(held.WriteState(RunLockState.AcquiredNow(Identifiers.NewRunId(), Session)).Ok);
            Assert.IsTrue(held.Release().Ok);
            Assert.AreEqual(
                0, Directory.GetFiles(_root, "*" + ".opweave-tmp").Length);
        }
    }
}
