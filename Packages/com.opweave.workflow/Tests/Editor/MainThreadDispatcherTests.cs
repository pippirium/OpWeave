#nullable enable

// T-W2.1-01 [Edit] tests: local:T-W2.1-01:dispatcher-probe — SPEC §G1 item 1
// and test 1. Work posted from another thread executes on the main thread at
// the head of EditorApplication.update: the posting thread only enqueues, and
// the drain — prepended to the update delegate chain at install — runs the
// item on the Editor's own update before callbacks subscribed after install.
// These tests must never invoke the EditorApplication.update delegate (or
// other invocation-list entries): raising it inside a test re-enters the
// Unity Test Framework's own update callback and corrupts the run's result
// collection. Ordering is asserted by inspecting the invocation list, and
// draining is driven through the internal MainThreadDispatcher.Drain seam.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class MainThreadDispatcherTests
    {
        [UnityTest]
        public IEnumerator Post_WorkPostedFromBackgroundThread_RunsOnMainThread()
        {
            var mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var executedThreadId = 0;
            var done = new ManualResetEventSlim();

            var worker = new Thread(() => MainThreadDispatcher.Post(() =>
            {
                executedThreadId = Thread.CurrentThread.ManagedThreadId;
                done.Set();
            }));
            worker.Start();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!done.IsSet && DateTime.UtcNow < deadline)
            {
                yield return null;
            }

            Assert.That(worker.Join(TimeSpan.FromSeconds(5)), Is.True,
                "The posting thread did not finish within the join timeout.");
            Assert.That(done.IsSet, Is.True, "The posted work did not run.");
            Assert.That(executedThreadId, Is.EqualTo(mainThreadId));
        }

        [Test]
        public void Install_DrainStaysFirstInUpdateInvocationList()
        {
            EditorApplication.CallbackFunction preExisting = () => { };
            EditorApplication.CallbackFunction afterInstall = () => { };
            EditorApplication.update += preExisting;
            try
            {
                MainThreadDispatcher.Install();
                EditorApplication.update += afterInstall;
                try
                {
                    var invocationList = EditorApplication.update.GetInvocationList();
                    var drain = (EditorApplication.CallbackFunction)MainThreadDispatcher.Drain;
                    Assert.That(invocationList[0].Method, Is.EqualTo(drain.Method),
                        "The dispatcher drain is not at the head of EditorApplication.update.");
                    Assert.That(invocationList, Has.Member(preExisting),
                        "The subscriber added before install was removed from EditorApplication.update.");
                    Assert.That(invocationList, Has.Member(afterInstall),
                        "The subscriber added after install is missing from EditorApplication.update.");
                }
                finally
                {
                    EditorApplication.update -= afterInstall;
                }
            }
            finally
            {
                EditorApplication.update -= preExisting;
            }
        }

        [Test]
        public void Post_WorkPosted_RunsOnTheNextDrainOnMainThread()
        {
            var mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var ran = false;
            var executedThreadId = 0;

            MainThreadDispatcher.Post(() =>
            {
                ran = true;
                executedThreadId = Thread.CurrentThread.ManagedThreadId;
            });

            MainThreadDispatcher.Drain();

            Assert.That(ran, Is.True, "The posted work did not run on the drain.");
            Assert.That(executedThreadId, Is.EqualTo(mainThreadId));
        }

        [Test]
        public void Post_WorkPostedDuringDrain_RunsAtTheNextDrain()
        {
            var calls = new List<string>();
            MainThreadDispatcher.Post(() =>
            {
                calls.Add("first");
                MainThreadDispatcher.Post(() => calls.Add("repost"));
            });

            MainThreadDispatcher.Drain();
            Assert.That(calls, Is.EqualTo(new[] { "first" }));

            MainThreadDispatcher.Drain();
            Assert.That(calls, Is.EqualTo(new[] { "first", "repost" }));
        }

        [Test]
        public void Post_NullWork_ThrowsArgumentNullException()
        {
            Assert.That(() => MainThreadDispatcher.Post(null!), Throws.ArgumentNullException);
        }
    }
}
