#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.3-09/T-W5.3-10 [Edit] tests (SPEC §H7 item 3, §0.6 SG-17): the
// shared transient-replace classifier (Win32 32/33/5/1175 under
// FACILITY_WIN32 or as a bare Win32 code), the shared bounded retry
// budget with exponential
// backoff, per-attempt re-verification on both replace paths — including
// the replacement file's re-verification after error 1175 — and
// fail-closed behaviour once the budget is used up.
// Every test that waits uses the injected clock and delay installed in
// SetUp, so no test sleeps for the real budget. Real-file tests run in a
// private temporary directory, not under Assets/. Below 6000.5 the
// OpWeave.GraphToolkit asmdef is excluded and this file compiles to nothing.
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpWeave.Core.Model;
using OpWeave.Core.Registry;
using OpWeave.GraphToolkit;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class GraphJsonSaveRetryTests
    {
        private const string IgnoreReason = "requires Windows file-sharing semantics";

        private string _dir = null!;
        private long _now;
        private List<int> _delays = null!;
        private Func<long>? _previousClock;
        private Action<int>? _previousSleep;
        private Func<RegistrySnapshot?>? _previousSource;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "../Temp/OpWeaveGraphJsonSaveRetryTests"));
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
            Directory.CreateDirectory(_dir);

            // The shared budget runs against a fake clock whose time only a
            // fake delay advances, so every wait is instant and the backoff
            // sequence is deterministic.
            _now = 0;
            _delays = new List<int>();
            _previousClock = GraphJsonFile.ReplaceRetryClock;
            _previousSleep = GraphJsonFile.ReplaceRetrySleep;
            GraphJsonFile.ReplaceRetryClock = () => _now;
            GraphJsonFile.ReplaceRetrySleep = delay =>
            {
                _delays.Add(delay);
                _now += delay;
            };

            _previousSource = GraphToolkitRegistry.SnapshotSource;
            var snapshot = GraphJsonConversionTests.BuildSnapshot();
            GraphToolkitRegistry.SnapshotSource = () => snapshot;
        }

        [TearDown]
        public void TearDown()
        {
            if (_previousSource is not null)
            {
                GraphToolkitRegistry.SnapshotSource = _previousSource;
                _previousSource = null;
            }
            if (_previousClock is not null)
            {
                GraphJsonFile.ReplaceRetryClock = _previousClock;
                _previousClock = null;
            }
            if (_previousSleep is not null)
            {
                GraphJsonFile.ReplaceRetrySleep = _previousSleep;
                _previousSleep = null;
            }
            if (Directory.Exists(_dir))
            {
                // A read-only hold left behind by a failed test cannot be
                // deleted on Windows.
                foreach (var file in Directory.GetFiles(_dir))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_dir, true);
            }
        }

        // ── Shared transient classifier ──────────────────────────────────

        [Test]
        public void Replace_TransientClassification_Retries32_33_5_1175()
        {
            var transient = new Exception[]
            {
                new IOException("sharing violation", unchecked((int)0x80070020)),
                new IOException("lock violation", unchecked((int)0x80070021)),
                new IOException("access denied", unchecked((int)0x80070005)),
                new IOException(
                    "unable to remove replaced", unchecked((int)0x80070497)),
                // The same Win32 errors as bare codes (0x0000xxxx), the
                // form new IOException(message, code) stores.
                new IOException("bare sharing violation", 0x20),
                new IOException("bare lock violation", 0x21),
                new IOException("bare access denied", 0x05),
                new IOException("bare unable to remove replaced", 0x497),
                new UnauthorizedAccessException(),
                new SimulatedAccessDenied(),
            };
            foreach (var e in transient)
            {
                Assert.IsTrue(
                    GraphJsonFile.IsTransientReplaceFailure(e),
                    e.GetType().Name + " (HResult 0x" +
                    e.HResult.ToString("X8") + ") must be transient");
            }

            var notTransient = new Exception[]
            {
                // ERROR_UNABLE_TO_MOVE_REPLACEMENT[2] (1176/1177) can leave
                // the destination removed or renamed — never retried.
                new IOException(
                    "unable to move replacement", unchecked((int)0x80070498)),
                new IOException(
                    "unable to move replacement 2", unchecked((int)0x80070499)),
                new IOException("disk full", unchecked((int)0x80070070)),
                new FileNotFoundException("file not found"),
                new DirectoryNotFoundException("directory not found"),
                new IOException("no win32 code"),
                // A Win32 error's low word under a non-Win32 facility is
                // not a Win32 error: 32, 33, 5 and 1175 under FACILITY_ITF
                // (0x8004xxxx) are not transient.
                new IOException("itf 32", unchecked((int)0x80040020)),
                new IOException("itf 33", unchecked((int)0x80040021)),
                new IOException("itf 5", unchecked((int)0x80040005)),
                new IOException("itf 1175", unchecked((int)0x80040497)),
            };
            foreach (var e in notTransient)
            {
                Assert.IsFalse(
                    GraphJsonFile.IsTransientReplaceFailure(e),
                    e.GetType().Name + " (HResult 0x" +
                    e.HResult.ToString("X8") + ") must not be retried");
            }
        }

        // ── Shared bounded budget ────────────────────────────────────────

        // With instant attempts the recorded delays must be exactly
        // 50, 100, 200, 400, 500, 500 ms over 7 attempts, then the call
        // fails closed; when each attempt itself takes time, the loop still
        // stops before any delay would take the total past 2000 ms.
        [Test]
        public void Replace_Backoff_DelaysWithinBudget()
        {
            var path = Path.Combine(_dir, "backoff.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            Assert.IsNull(disk.ErrorMessage);
            Assert.IsNotNull(disk.BytesHash);
            var output = GraphJsonFile.BuildOutput(Read(Fixture), disk.Text);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                _ =>
                {
                    attempts++;
                    throw new IOException(
                        "simulated sharing violation", unchecked((int)0x80070020));
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.IoError,
                result.Status,
                "the call fails closed once the budget is used up");
            Assert.IsNotNull(result.ErrorMessage,
                "the failure carries the last exception's message");
            CollectionAssert.AreEqual(
                new[] { 50, 100, 200, 400, 500, 500 },
                _delays,
                "the backoff doubles from 50 ms, caps at 500 ms per delay " +
                "and stays within the 2000 ms budget");
            Assert.AreEqual(7, attempts);
            Assert.AreEqual(Fixture, File.ReadAllText(path),
                "the destination is left untouched");
            Assert.IsEmpty(
                Directory.GetFiles(_dir, "*.tmp"),
                "the temp file is cleaned up");

            // Variant: each attempt also advances the clock. The loop still
            // stops before any delay would exceed the 2000 ms budget —
            // here attempt time alone consumes it after four sleeps.
            _delays.Clear();
            attempts = 0;
            var timed = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                _ =>
                {
                    attempts++;
                    _now += 300;
                    throw new IOException(
                        "simulated sharing violation", unchecked((int)0x80070020));
                });

            Assert.AreEqual(GraphJsonPublishStatus.IoError, timed.Status);
            Assert.AreEqual(5, attempts);
            CollectionAssert.AreEqual(
                new[] { 50, 100, 200, 400 },
                _delays,
                "attempt time counts toward the budget: elapsed 2050 ms + " +
                "the next 500 ms delay would pass 2000 ms");
        }

        // ── Simulated transient failures (all hosts) ─────────────────────

        [TestCase(0x20)]
        [TestCase(0x21)]
        public void Save_SharingOrLockViolationReleased_Saved(int win32Error)
        {
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(
                    _dir, "share-" + win32Error + ".opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var expected = ExpectedOutput(graph, baseline);
                var attempts = 0;

                var result = GraphJsonFile.SaveToFile(
                    graph,
                    path,
                    baseline,
                    explicitOverwrite: false,
                    beforeReplace: _ =>
                    {
                        if (attempts++ == 0)
                        {
                            throw new IOException(
                                "simulated hold",
                                unchecked((int)0x80070000) | win32Error);
                        }
                    });

                Assert.AreEqual(
                    GraphJsonSaveStatus.Saved,
                    result.Status,
                    "error: " + result.ErrorMessage);
                Assert.AreEqual(2, attempts, "the transient hold is retried once");
                CollectionAssert.AreEqual(expected.Bytes, File.ReadAllBytes(path));
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            });
        }

        [Test]
        public void PublishReplace_AccessDeniedSimulated_RetriedAndPublishes()
        {
            var path = Path.Combine(_dir, "denied.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                _ =>
                {
                    if (attempts++ == 0)
                    {
                        throw new SimulatedAccessDenied();
                    }
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.Published,
                result.Status,
                "error: " + result.ErrorMessage);
            CollectionAssert.AreEqual(output.Bytes, File.ReadAllBytes(path));
            Assert.AreEqual(2, attempts, "the transient hold is retried once");
        }

        [Test]
        public void PublishReplace_AccessDeniedThenChanged_Refused()
        {
            var path = Path.Combine(_dir, "denied-changed.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                destination =>
                {
                    attempts++;
                    File.WriteAllText(destination, "external edit");
                    throw new SimulatedAccessDenied();
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.ChangedSinceRead,
                result.Status,
                "a hold that releases into new content refuses the replace");
            Assert.AreEqual(
                "external edit",
                File.ReadAllText(path),
                "the external bytes are left untouched");
            Assert.AreEqual(
                1,
                attempts,
                "the second attempt refuses at re-verification, before the seam");
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Save_DestinationChangedDuringRetry_NotOverwritten(bool explicitOverwrite)
        {
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(
                    _dir, "changed-" + explicitOverwrite + ".opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var external = Fixture.Replace(
                    "\"id\": \"test.retry\"", "\"id\": \"test.retry.external\"");
                var attempts = 0;

                var result = GraphJsonFile.SaveToFile(
                    graph,
                    path,
                    baseline,
                    explicitOverwrite,
                    beforeReplace: destination =>
                    {
                        attempts++;
                        File.WriteAllText(destination, external);
                        throw new SimulatedAccessDenied();
                    });

                Assert.AreEqual(
                    GraphJsonSaveStatus.ExternalModification,
                    result.Status,
                    "error: " + result.ErrorMessage);
                Assert.AreEqual(
                    external,
                    File.ReadAllText(path),
                    "the external bytes are left intact");
                Assert.AreEqual(
                    1,
                    attempts,
                    "the second attempt refuses at re-verification, before the seam");
                Assert.IsNotNull(
                    result.Document,
                    "the result carries the newly read on-disk document");
                Assert.AreEqual(
                    Read(external).Canonical.WorkflowHash,
                    result.Document!.Canonical.WorkflowHash);
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            });
        }

        [Test]
        public void Save_DestinationDeletedDuringRetry_NotRecreated()
        {
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(_dir, "deleted.opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var attempts = 0;

                var result = GraphJsonFile.SaveToFile(
                    graph,
                    path,
                    baseline,
                    explicitOverwrite: false,
                    beforeReplace: destination =>
                    {
                        attempts++;
                        File.Delete(destination);
                        throw new SimulatedAccessDenied();
                    });

                Assert.AreEqual(
                    GraphJsonSaveStatus.ExternalModification,
                    result.Status,
                    "error: " + result.ErrorMessage);
                Assert.IsFalse(
                    File.Exists(path),
                    "the vanished destination is never recreated");
                Assert.IsNull(result.Document);
                Assert.AreEqual(
                    1,
                    attempts,
                    "the second attempt refuses at re-verification, before the seam");
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            });
        }

        // ── Error 1175 (ERROR_UNABLE_TO_REMOVE_REPLACED) ──────────────────
        // Simulated deterministically through the before-replace seam: it
        // throws an IOException with HResult 0x80070497 and the real
        // Windows message. The shared budget, backoff and destination
        // re-verification are T-W5.3-09's, unchanged; after a 1175 failure
        // the temp replacement file is additionally re-verified before the
        // next attempt.

        [Test]
        public void Save_Error1175Once_RetriedAndSaved()
        {
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(_dir, "1175-once.opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var expected = ExpectedOutput(graph, baseline);
                var attempts = 0;

                var result = GraphJsonFile.SaveToFile(
                    graph,
                    path,
                    baseline,
                    explicitOverwrite: false,
                    beforeReplace: _ =>
                    {
                        if (attempts++ == 0)
                        {
                            throw UnableToRemoveReplaced();
                        }
                    });

                Assert.AreEqual(
                    GraphJsonSaveStatus.Saved,
                    result.Status,
                    "error: " + result.ErrorMessage);
                Assert.AreEqual(2, attempts, "the 1175 failure is retried once");
                CollectionAssert.AreEqual(expected.Bytes, File.ReadAllBytes(path));
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            });
        }

        [Test]
        public void PublishReplace_Error1175Once_RetriedAndPublishes()
        {
            var path = Path.Combine(_dir, "1175-once.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                _ =>
                {
                    if (attempts++ == 0)
                    {
                        throw UnableToRemoveReplaced();
                    }
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.Published,
                result.Status,
                "error: " + result.ErrorMessage);
            Assert.AreEqual(2, attempts, "the 1175 failure is retried once");
            CollectionAssert.AreEqual(output.Bytes, File.ReadAllBytes(path));
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Test]
        public void Save_Error1175Always_IoErrorAfterBudget()
        {
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(_dir, "1175-always.opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var attempts = 0;

                var result = GraphJsonFile.SaveToFile(
                    graph,
                    path,
                    baseline,
                    explicitOverwrite: false,
                    beforeReplace: _ =>
                    {
                        attempts++;
                        throw UnableToRemoveReplaced();
                    });

                Assert.AreEqual(
                    GraphJsonSaveStatus.IoError,
                    result.Status,
                    "the call fails closed once the budget is used up");
                Assert.IsNotNull(
                    result.ErrorMessage,
                    "the failure carries the last exception's message");
                StringAssert.Contains(
                    "Unable to remove the file to be replaced.",
                    result.ErrorMessage!,
                    "the message carries the 1175 text");
                Assert.AreEqual(7, attempts);
                CollectionAssert.AreEqual(
                    new[] { 50, 100, 200, 400, 500, 500 },
                    _delays,
                    "the unchanged backoff applies to 1175");
                Assert.AreEqual(
                    Fixture,
                    File.ReadAllText(path),
                    "the destination's bytes are unchanged");
                Assert.IsEmpty(
                    Directory.GetFiles(_dir, "*.tmp"),
                    "no temp file remains");
            });
        }

        [Test]
        public void PublishReplace_Error1175Always_IoErrorAfterBudget()
        {
            var path = Path.Combine(_dir, "1175-always.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                _ =>
                {
                    attempts++;
                    throw UnableToRemoveReplaced();
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.IoError,
                result.Status,
                "the call fails closed once the budget is used up");
            Assert.IsNotNull(
                result.ErrorMessage,
                "the failure carries the last exception's message");
            StringAssert.Contains(
                "Unable to remove the file to be replaced.",
                result.ErrorMessage!,
                "the message carries the 1175 text");
            Assert.AreEqual(7, attempts);
            CollectionAssert.AreEqual(
                new[] { 50, 100, 200, 400, 500, 500 },
                _delays,
                "the unchanged backoff applies to 1175");
            Assert.AreEqual(
                Fixture,
                File.ReadAllText(path),
                "the destination's bytes are unchanged");
            Assert.IsEmpty(
                Directory.GetFiles(_dir, "*.tmp"),
                "no temp file remains");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Save_Error1175ThenExternalChange_ExternalModification(
            bool explicitOverwrite)
        {
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(
                    _dir, "1175-changed-" + explicitOverwrite + ".opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var external = Fixture.Replace(
                    "\"id\": \"test.retry\"", "\"id\": \"test.retry.external\"");
                var attempts = 0;

                var result = GraphJsonFile.SaveToFile(
                    graph,
                    path,
                    baseline,
                    explicitOverwrite,
                    beforeReplace: destination =>
                    {
                        if (attempts++ == 0)
                        {
                            // The 1175 failure released into an external
                            // edit: the next attempt refuses it at
                            // re-verification.
                            File.WriteAllText(destination, external);
                            throw UnableToRemoveReplaced();
                        }
                    });

                Assert.AreEqual(
                    GraphJsonSaveStatus.ExternalModification,
                    result.Status,
                    "error: " + result.ErrorMessage);
                Assert.AreEqual(
                    external,
                    File.ReadAllText(path),
                    "the external bytes are left intact");
                Assert.AreEqual(
                    1,
                    attempts,
                    "the second attempt refuses at re-verification, before the seam");
                Assert.IsNotNull(
                    result.Document,
                    "the result carries the newly read on-disk document");
                Assert.AreEqual(
                    Read(external).Canonical.WorkflowHash,
                    result.Document!.Canonical.WorkflowHash);
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            });
        }

        [Test]
        public void PublishReplace_Error1175ThenExternalChange_ChangedSinceRead()
        {
            var path = Path.Combine(_dir, "1175-changed.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                destination =>
                {
                    if (attempts++ == 0)
                    {
                        File.WriteAllText(destination, "external edit");
                        throw UnableToRemoveReplaced();
                    }
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.ChangedSinceRead,
                result.Status,
                "a 1175 failure that releases into new content refuses the replace");
            Assert.AreEqual(
                "external edit",
                File.ReadAllText(path),
                "the external bytes are left untouched");
            Assert.AreEqual(
                1,
                attempts,
                "the second attempt refuses at re-verification, before the seam");
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Test]
        public void Save_Error1175ThenDeleted_ExternalModification()
        {
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(_dir, "1175-deleted.opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var attempts = 0;

                var result = GraphJsonFile.SaveToFile(
                    graph,
                    path,
                    baseline,
                    explicitOverwrite: false,
                    beforeReplace: destination =>
                    {
                        if (attempts++ == 0)
                        {
                            File.Delete(destination);
                            throw UnableToRemoveReplaced();
                        }
                    });

                Assert.AreEqual(
                    GraphJsonSaveStatus.ExternalModification,
                    result.Status,
                    "error: " + result.ErrorMessage);
                Assert.IsFalse(
                    File.Exists(path),
                    "the vanished destination is never recreated");
                Assert.AreEqual(
                    1,
                    attempts,
                    "the second attempt refuses at re-verification, before the seam");
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            });
        }

        [Test]
        public void PublishReplace_Error1175ThenDeleted_ChangedSinceRead()
        {
            var path = Path.Combine(_dir, "1175-deleted.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                destination =>
                {
                    if (attempts++ == 0)
                    {
                        File.Delete(destination);
                        throw UnableToRemoveReplaced();
                    }
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.ChangedSinceRead,
                result.Status,
                "a 1175 failure that releases into a vanished file refuses the replace");
            Assert.IsFalse(
                File.Exists(path),
                "the vanished destination is never recreated");
            Assert.AreEqual(
                1,
                attempts,
                "the second attempt refuses at re-verification, before the seam");
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        // A 1175 failure preserves the files' names only — the replacement
        // file's contents are unproven. The seam alters the temp file's
        // bytes before throwing: the next attempt must publish exactly the
        // intended bytes or fail closed, never the altered ones. This
        // implementation re-verifies the replacement, so it fails closed.
        [Test]
        public void Save_Error1175ThenReplacementAltered_NeverPublishesAlteredBytes()
        {
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(_dir, "1175-altered.opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var attempts = 0;

                var result = GraphJsonFile.SaveToFile(
                    graph,
                    path,
                    baseline,
                    explicitOverwrite: false,
                    beforeReplace: _ =>
                    {
                        if (attempts++ == 0)
                        {
                            File.WriteAllText(
                                SingleTempFile(), "tampered replacement");
                            throw UnableToRemoveReplaced();
                        }
                    });

                Assert.AreEqual(
                    GraphJsonSaveStatus.IoError,
                    result.Status,
                    "an unprovable replacement fails closed");
                Assert.IsNotNull(result.ErrorMessage);
                Assert.AreEqual(
                    Fixture,
                    File.ReadAllText(path),
                    "the destination is never replaced from the altered temp file");
                Assert.AreEqual(
                    1,
                    attempts,
                    "the next attempt fails at replacement re-verification, before the seam");
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            });
        }

        [Test]
        public void PublishReplace_Error1175ThenReplacementAltered_NeverPublishesAlteredBytes()
        {
            var path = Path.Combine(_dir, "1175-altered.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                _ =>
                {
                    if (attempts++ == 0)
                    {
                        File.WriteAllText(
                            SingleTempFile(), "tampered replacement");
                        throw UnableToRemoveReplaced();
                    }
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.IoError,
                result.Status,
                "an unprovable replacement fails closed");
            Assert.IsNotNull(result.ErrorMessage);
            Assert.AreEqual(
                Fixture,
                File.ReadAllText(path),
                "the destination is never replaced from the altered temp file");
            Assert.AreEqual(
                1,
                attempts,
                "the next attempt fails at replacement re-verification, before the seam");
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        // A temp file absent after 1175 can never be proven to hold the
        // intended bytes — the call must fail closed.
        [Test]
        public void Save_Error1175ThenReplacementDeleted_IoError()
        {
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(_dir, "1175-temp-deleted.opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var attempts = 0;

                var result = GraphJsonFile.SaveToFile(
                    graph,
                    path,
                    baseline,
                    explicitOverwrite: false,
                    beforeReplace: _ =>
                    {
                        if (attempts++ == 0)
                        {
                            File.Delete(SingleTempFile());
                            throw UnableToRemoveReplaced();
                        }
                    });

                Assert.AreEqual(
                    GraphJsonSaveStatus.IoError,
                    result.Status,
                    "an absent replacement fails closed");
                Assert.IsNotNull(result.ErrorMessage);
                Assert.AreEqual(
                    Fixture,
                    File.ReadAllText(path),
                    "the destination's bytes are unchanged");
                Assert.AreEqual(1, attempts);
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            });
        }

        [Test]
        public void PublishReplace_Error1175ThenReplacementDeleted_IoError()
        {
            var path = Path.Combine(_dir, "1175-temp-deleted.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                _ =>
                {
                    if (attempts++ == 0)
                    {
                        File.Delete(SingleTempFile());
                        throw UnableToRemoveReplaced();
                    }
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.IoError,
                result.Status,
                "an absent replacement fails closed");
            Assert.IsNotNull(result.ErrorMessage);
            Assert.AreEqual(
                Fixture,
                File.ReadAllText(path),
                "the destination's bytes are unchanged");
            Assert.AreEqual(1, attempts);
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        // Errors 1176/1177 (ERROR_UNABLE_TO_MOVE_REPLACEMENT[2]) can leave
        // the destination removed, renamed or carrying merged streams —
        // they stay non-retryable and end the call on the first failure.
        [TestCase(0x0498)]
        [TestCase(0x0499)]
        public void Replace_Error1176And1177_NotRetried(int win32Error)
        {
            var path = Path.Combine(
                _dir, "move-replacement-" + win32Error + ".opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            var attempts = 0;

            var result = GraphJsonFile.PublishReplace(
                path,
                output,
                disk.BytesHash!,
                _ =>
                {
                    attempts++;
                    throw new IOException(
                        "simulated unable to move replacement",
                        unchecked((int)0x80070000) | win32Error);
                });

            Assert.AreEqual(
                GraphJsonPublishStatus.IoError,
                result.Status,
                "errors 1176/1177 are never retried");
            Assert.IsNotNull(result.ErrorMessage);
            Assert.AreEqual(1, attempts, "the call ends on the first failure");
            Assert.IsEmpty(_delays, "no retry delay was slept");
            Assert.AreEqual(
                Fixture,
                File.ReadAllText(path),
                "the destination's bytes are unchanged");
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        // ── Windows real-hold tests ──────────────────────────────────────
        // The hold is the read-only attribute on the destination, which
        // makes File.Replace fail with access denied (HResult 0x80070005);
        // a direct probe asserts that failure so the test cannot pass
        // vacuously.

        [Test]
        public void Save_AccessDeniedHoldReleased_Saved()
        {
            if (!IsWindows)
            {
                Assert.Ignore(IgnoreReason);
            }
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(_dir, "held-save.opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                var expected = ExpectedOutput(graph, baseline);
                File.SetAttributes(path, FileAttributes.ReadOnly);
                try
                {
                    AssertHoldDeniesReplace(path);
                    var attempts = 0;

                    var result = GraphJsonFile.SaveToFile(
                        graph,
                        path,
                        baseline,
                        explicitOverwrite: false,
                        beforeReplace: destination =>
                        {
                            if (attempts++ == 1)
                            {
                                File.SetAttributes(
                                    destination, FileAttributes.Normal);
                            }
                        });

                    Assert.AreEqual(
                        GraphJsonSaveStatus.Saved,
                        result.Status,
                        "error: " + result.ErrorMessage);
                    Assert.AreEqual(
                        2,
                        attempts,
                        "the transient access-denied is retried once");
                    CollectionAssert.AreEqual(
                        expected.Bytes, File.ReadAllBytes(path));
                    Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
                }
                finally
                {
                    ReleaseHold(path);
                }
            });
        }

        [Test]
        public void Save_AccessDeniedHoldNeverReleased_IoErrorAfterBudget()
        {
            if (!IsWindows)
            {
                Assert.Ignore(IgnoreReason);
            }
            GraphJsonConversionTests.WithGraph(graph =>
            {
                var path = Path.Combine(_dir, "held-save-forever.opweave.json");
                var baseline = ImportBaseline(graph);
                File.WriteAllText(path, Fixture);
                File.SetAttributes(path, FileAttributes.ReadOnly);
                try
                {
                    AssertHoldDeniesReplace(path);
                    var attempts = 0;

                    var result = GraphJsonFile.SaveToFile(
                        graph,
                        path,
                        baseline,
                        explicitOverwrite: false,
                        beforeReplace: _ => attempts++);

                    Assert.AreEqual(
                        GraphJsonSaveStatus.IoError,
                        result.Status,
                        "the call fails closed once the budget is used up");
                    Assert.IsNotNull(
                        result.ErrorMessage,
                        "the failure carries the last exception's message");
                    Assert.AreEqual(7, attempts);
                    CollectionAssert.AreEqual(
                        new[] { 50, 100, 200, 400, 500, 500 },
                        _delays,
                        "no delay pushes the total past the 2000 ms budget");
                    Assert.AreEqual(
                        Fixture,
                        File.ReadAllText(path),
                        "the destination's bytes are unchanged");
                    Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
                }
                finally
                {
                    ReleaseHold(path);
                }
            });
        }

        [Test]
        public void PublishReplace_AccessDeniedHoldReleased_Publishes()
        {
            if (!IsWindows)
            {
                Assert.Ignore(IgnoreReason);
            }
            var path = Path.Combine(_dir, "held-publish.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                AssertHoldDeniesReplace(path);
                var attempts = 0;

                var result = GraphJsonFile.PublishReplace(
                    path,
                    output,
                    disk.BytesHash!,
                    destination =>
                    {
                        if (attempts++ == 1)
                        {
                            File.SetAttributes(destination, FileAttributes.Normal);
                        }
                    });

                Assert.AreEqual(
                    GraphJsonPublishStatus.Published,
                    result.Status,
                    "error: " + result.ErrorMessage);
                Assert.AreEqual(
                    2,
                    attempts,
                    "the transient access-denied is retried once");
                CollectionAssert.AreEqual(output.Bytes, File.ReadAllBytes(path));
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            }
            finally
            {
                ReleaseHold(path);
            }
        }

        [Test]
        public void PublishReplace_AccessDeniedHoldNeverReleased_IoErrorAfterBudget()
        {
            if (!IsWindows)
            {
                Assert.Ignore(IgnoreReason);
            }
            var path = Path.Combine(_dir, "held-publish-forever.opweave.json");
            File.WriteAllText(path, Fixture);
            var disk = GraphJsonFile.Read(path);
            var output = ChangedOutput(disk);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                AssertHoldDeniesReplace(path);
                var attempts = 0;

                var result = GraphJsonFile.PublishReplace(
                    path,
                    output,
                    disk.BytesHash!,
                    _ => attempts++);

                Assert.AreEqual(
                    GraphJsonPublishStatus.IoError,
                    result.Status,
                    "the call fails closed once the budget is used up");
                Assert.IsNotNull(
                    result.ErrorMessage,
                    "the failure carries the last exception's message");
                Assert.AreEqual(7, attempts);
                Assert.AreEqual(
                    Fixture,
                    File.ReadAllText(path),
                    "the destination's bytes are unchanged");
                Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
            }
            finally
            {
                ReleaseHold(path);
            }
        }

        // ── helpers ──────────────────────────────────────────────────────

        private static bool IsWindows =>
            Application.platform == RuntimePlatform.WindowsEditor;

        // Asserts that a direct File.Replace on the held destination fails
        // with access denied (HResult 0x80070005), so a real-hold test
        // cannot pass vacuously.
        private static void AssertHoldDeniesReplace(string path)
        {
            var probePath = path + ".holdprobe.tmp";
            File.WriteAllText(probePath, "probe");
            Exception? probe = null;
            try
            {
                File.Replace(probePath, path, null);
            }
            catch (Exception e)
            {
                probe = e;
            }
            finally
            {
                if (File.Exists(probePath))
                {
                    File.SetAttributes(probePath, FileAttributes.Normal);
                    File.Delete(probePath);
                }
            }
            Assert.IsNotNull(probe, "the hold must make File.Replace fail");
            Assert.AreEqual(
                unchecked((int)0x80070005),
                probe!.HResult,
                "the hold must produce access denied (0x80070005), got 0x" +
                probe!.HResult.ToString("X8"));
        }

        private static void ReleaseHold(string path)
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }

        private static GraphJsonFile.Output ChangedOutput(GraphJsonFile.DiskRead disk)
        {
            Assert.IsNull(disk.ErrorMessage);
            Assert.IsNotNull(disk.BytesHash);
            var changed = Fixture.Replace(
                "\"operation\": \"core.start\"", "\"operation\": \"core.start.upd\"");
            return GraphJsonFile.BuildOutput(Read(changed), disk.Text);
        }

        private static WorkflowDocument ImportBaseline(OpWeaveGraph graph)
        {
            var imported = GraphJsonConverter.Import(graph, Fixture);
            Assert.IsTrue(imported.Ok, "the fixture must import");
            return imported.Value!;
        }

        // The bytes a clean SaveToFile writes for the same graph and disk
        // text — the expected bytes of a successful save.
        private static GraphJsonFile.Output ExpectedOutput(
            OpWeaveGraph graph, WorkflowDocument baseline)
        {
            var exported = GraphJsonConverter.Export(graph, baseline);
            Assert.IsTrue(exported.Ok, "the fixture graph must export");
            return GraphJsonFile.BuildOutput(exported.Value!, Fixture);
        }

        private static WorkflowDocument Read(string text)
        {
            var result = WorkflowSerializer.Read(text);
            Assert.IsTrue(
                result.Ok,
                "fixture must parse: " + string.Join(", ", result.Diagnostics));
            return result.Value!;
        }

        // The single temp file the in-flight replace left in the private
        // test directory — the seams expose the destination path only, so
        // the altered/absent-replacement tests locate it by its extension.
        private string SingleTempFile()
        {
            var temps = Directory.GetFiles(_dir, "*.tmp");
            Assert.AreEqual(
                1,
                temps.Length,
                "the replace's temp file is the only temp file");
            return temps[0];
        }

        // An IOException simulating Win32 error 1175
        // (ERROR_UNABLE_TO_REMOVE_REPLACED), as File.Replace raises when
        // the replaced file cannot be deleted: HResult 0x80070497 and the
        // real Windows message.
        private static IOException UnableToRemoveReplaced() =>
            new IOException(
                "Unable to remove the file to be replaced.",
                unchecked((int)0x80070497));

        // An UnauthorizedAccessException carrying the Win32 access-denied
        // HResult 0x80070005, as File.Replace raises on a held target.
        private sealed class SimulatedAccessDenied : UnauthorizedAccessException
        {
            internal SimulatedAccessDenied()
            {
                HResult = unchecked((int)0x80070005);
            }
        }

        private const string Fixture = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.retry"",
  ""nodes"": [
    {
      ""id"": ""start"",
      ""operation"": ""core.start"",
      ""version"": 1
    }
  ],
  ""controlEdges"": []
}
";
    }
}
#endif
