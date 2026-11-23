#nullable enable

// T-W4.1-01 [Edit] tests: the SPEC §G4 compile-error capture — the
// pipeline's per-assembly callback mirrors each error into SessionState
// (the domain-reload half) and journals it as a step-attributed F6 log
// record with an immediate flush (the Editor-restart half); the
// completion counter fed by compilationFinished and
// assemblyCompilationNotRequired is the L-05 no-change signal.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// SPEC §G4: compile errors reach SessionState and the journal in the
    /// pipeline callback; <see cref="CompileErrorCapture.RecentErrors"/>
    /// merges the journaled half back for the resumed wait.
    /// </summary>
    internal sealed class CompileErrorCaptureTests
    {
        private static readonly CompilerMessage Error = new CompilerMessage
        {
            type = CompilerMessageType.Error,
            file = "Assets/Broken.cs",
            line = 3,
            column = 7,
            message = "error CS1002: ; expected",
        };

        private static readonly CompilerMessage Warning = new CompilerMessage
        {
            type = CompilerMessageType.Warning,
            file = "Assets/Noisy.cs",
            line = 1,
            column = 1,
            message = "warning CS0618: obsolete",
        };

        [SetUp]
        public void SetUp() => CompileErrorCapture.Clear();

        [TearDown]
        public void TearDown() => CompileErrorCapture.Clear();

        [Test]
        public void FinishedCount_ReadsSessionState()
        {
            var before = CompileErrorCapture.FinishedCount;
            try
            {
                SessionState.SetInt(CompileErrorCapture.FinishedKey, 42);
                Assert.That(CompileErrorCapture.FinishedCount, Is.EqualTo(42));
            }
            finally
            {
                SessionState.SetInt(CompileErrorCapture.FinishedKey, before);
            }
        }

        [Test]
        public void ErrorWhileWaiting_IsMirroredIntoRecentErrors()
        {
            CompileErrorCapture.BeginStep();
            CompileErrorCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Error });
            var recent = CompileErrorCapture.RecentErrors;
            Assert.That(recent.Count, Is.EqualTo(1));
            Assert.That(recent[0], Does.Contain("Broken.cs"));
            Assert.That(recent[0], Does.Contain("CS1002"));
        }

        [Test]
        public void ErrorWhileWaiting_IsMirroredIntoSessionState()
        {
            // The SPEC §G4 "その場で SessionState に書く": the slot is
            // persisted inside the callback, before any later call site
            // could run.
            CompileErrorCapture.BeginStep();
            CompileErrorCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Error });
            var stored = SessionState.GetString("OpWeave.CompileErrors", "");
            Assert.That(stored, Does.Contain("CS1002"));
        }

        [Test]
        public void WarningsAndInfo_AreNotCaptured()
        {
            CompileErrorCapture.BeginStep();
            CompileErrorCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Warning, Info() });
            Assert.That(CompileErrorCapture.RecentErrors, Is.Empty);
        }

        [Test]
        public void WindowClosed_NothingCaptured()
        {
            // Errors a step did not request belong to the F6 Console
            // channel, not this wait's OW-RUN-009.
            CompileErrorCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Error });
            Assert.That(CompileErrorCapture.RecentErrors, Is.Empty);
        }

        [Test]
        public void BeginStep_DropsPriorCapture()
        {
            CompileErrorCapture.BeginStep();
            CompileErrorCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Error });
            CompileErrorCapture.EndStep();
            CompileErrorCapture.BeginStep();
            Assert.That(CompileErrorCapture.RecentErrors, Is.Empty);
        }

        [Test]
        public void Mirror_SurvivesSimulatedDomainReload()
        {
            CompileErrorCapture.BeginStep();
            CompileErrorCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Error });
            CompileErrorCapture.SimulateDomainReload();
            var recent = CompileErrorCapture.RecentErrors;
            Assert.That(recent.Count, Is.EqualTo(1));
            Assert.That(recent[0], Does.Contain("CS1002"));
        }

        [Test]
        public void WaitWindow_SurvivesSimulatedDomainReload()
        {
            CompileErrorCapture.BeginStep();
            CompileErrorCapture.SimulateDomainReload();
            Assert.That(CompileErrorCapture.WaitWindowOpen, Is.True);
            CompileErrorCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Error });
            Assert.That(CompileErrorCapture.RecentErrors.Count, Is.EqualTo(1));
        }

        [Test]
        public void ErrorWhileWaiting_IsJournaledThroughBoundSinkAndFlushed()
        {
            var sink = new CollectingSink();
            var flushes = 0;
            var journal = Path.Combine(
                Path.GetTempPath(), "opweave-compile-journal-" +
                Guid.NewGuid().ToString("N") + ".ndjson");
            CompileErrorCapture.BindStepLog(journal, 3, sink, () => flushes++);
            CompileErrorCapture.BeginStep();
            CompileErrorCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Error });
            Assert.That(sink.Records.Count, Is.EqualTo(1));
            Assert.That(sink.Records[0].Level, Is.EqualTo(Severity.Error));
            Assert.That(sink.Records[0].Message, Does.Contain("CS1002"));
            var data = (JObject)sink.Records[0].Data!.ToToken();
            Assert.That((string)data["source"]!, Is.EqualTo("unity_compile"));
            Assert.That(flushes, Is.EqualTo(1),
                "the captured error must be durable at append time (F3)");
        }

        [Test]
        public void UnboundStepLog_DoesNotJournal()
        {
            var sink = new CollectingSink();
            CompileErrorCapture.BindStepLog(null, -1, sink, null);
            CompileErrorCapture.BeginStep();
            CompileErrorCapture.OnAssemblyCompilationFinished(
                "probe.dll", new[] { Error });
            Assert.That(sink.Records, Is.Empty);
            Assert.That(CompileErrorCapture.RecentErrors.Count, Is.EqualTo(1),
                "the mirror still captures when no journal is bound");
        }

        [Test]
        public void RecentErrors_ReadsJournaledEntriesBack()
        {
            // The restart-durable half: records the bound sink journaled are
            // read back out of the journal file even when the mirror is
            // empty — the mirror dies with the Editor session, the journal
            // does not.
            var journal = WriteJournal(out _);
            try
            {
                CompileErrorCapture.BindStepLog(journal, 3, null, null);
                var recent = CompileErrorCapture.RecentErrors;
                Assert.That(recent, Has.Count.EqualTo(2));
                Assert.That(recent[0], Does.Contain("journaled-console-error"));
                Assert.That(recent[1], Does.Contain("journaled-compile-error"));
            }
            finally
            {
                File.Delete(journal);
            }
        }

        [Test]
        public void RecentErrors_MergesMirrorAndJournal_Deduped()
        {
            var journal = WriteJournal(out _);
            try
            {
                CompileErrorCapture.BindStepLog(journal, 3, null, null);
                CompileErrorCapture.BeginStep();
                CompileErrorCapture.OnAssemblyCompilationFinished(
                    "probe.dll", new[] { Error });
                var recent = CompileErrorCapture.RecentErrors;
                Assert.That(recent, Has.Count.EqualTo(3),
                    "two journaled entries plus the mirrored one");
            }
            finally
            {
                File.Delete(journal);
            }
        }

        [Test]
        public void Format_ComposesFileLineColumn()
        {
            Assert.That(
                CompileErrorCapture.Format(Error),
                Is.EqualTo(
                    "Assets/Broken.cs(3,7): error CS1002: ; expected"));
        }

        [Test]
        public void Format_MessageAlreadyLocated_IsKept()
        {
            var message = new CompilerMessage
            {
                type = CompilerMessageType.Error,
                file = "Assets/Broken.cs",
                line = 3,
                column = 7,
                message = "Assets/Broken.cs(3,7): error CS1002: ; expected",
            };
            Assert.That(
                CompileErrorCapture.Format(message),
                Is.EqualTo("Assets/Broken.cs(3,7): error CS1002: ; expected"));
        }

        [Test]
        public void Format_NoFile_KeepsText()
        {
            var message = new CompilerMessage
            {
                type = CompilerMessageType.Error,
                file = "",
                line = 0,
                column = 0,
                message = "error CS0000: internal",
            };
            Assert.That(
                CompileErrorCapture.Format(message),
                Is.EqualTo("error CS0000: internal"));
        }

        private static CompilerMessage Info() => new CompilerMessage
        {
            type = CompilerMessageType.Info,
            file = "",
            line = 0,
            column = 0,
            message = "info",
        };

        // A real journal: header plus the step-3 error records a bound
        // step wrote — one through the F6 Console channel, one through
        // the unity_compile channel — alongside records the read-back
        // must skip (wrong step, wrong source, wrong level).
        private static string WriteJournal(out string path)
        {
            path = Path.Combine(
                Path.GetTempPath(), "opweave-journal-" +
                Guid.NewGuid().ToString("N") + ".ndjson");
            var session = "session-" + Guid.NewGuid().ToString("N");
            var lines = new List<string>
            {
                JournalCodec.WriteHeader(
                    new JournalHeader("run-test", "planhash-test")),
            };
            var seq = 1;
            lines.Add(Record(seq++, session, 3, "error",
                "journaled-console-error", "unity_console"));
            lines.Add(Record(seq++, session, 3, "error",
                "journaled-compile-error", "unity_compile"));
            lines.Add(Record(seq++, session, 4, "error",
                "other-step-error", "unity_compile"));
            lines.Add(Record(seq++, session, 3, "warning",
                "not-an-error-level", "unity_compile"));
            lines.Add(Record(seq++, session, 3, "error",
                "wrong-source", "unity_import"));
            File.WriteAllLines(path, lines);
            return path;
        }

        private static string Record(
            int seq, string session, int step, string level,
            string message, string source) =>
            JournalCodec.WriteRecord(new JournalRecord(
                seq, UtcTimestamp.Now(), session, JournalRecordType.Log,
                step, null,
                JsonValue.From(new JObject
                {
                    ["level"] = level,
                    ["message"] = message,
                    ["data"] = new JObject { ["source"] = source },
                })));

        private sealed class CollectingSink : IRunLogSink
        {
            internal readonly List<(Severity Level, string Message, JsonValue? Data)>
                Records = new List<(Severity, string, JsonValue?)>();

            public void Write(
                Severity level, string message, string? code = null,
                JsonValue? data = null) =>
                Records.Add((level, message, data));
        }
    }
}
