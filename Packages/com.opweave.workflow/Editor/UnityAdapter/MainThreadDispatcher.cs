#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The cross-thread entry point of SPEC §G1 item 1: Pipeline commands run
    /// on the main thread by default, so work arriving on any other thread is
    /// queued through <see cref="Post"/> and executed at the head of
    /// <see cref="EditorApplication.update"/> — before the per-tick OpWeave
    /// processing (the run loop of SPEC §E3) that subscribes later.
    /// </summary>
    /// <remarks>
    /// <para><b>Head of the update.</b> Installation prepends the drain
    /// callback to the <see cref="EditorApplication.update"/> delegate chain,
    /// so queued work runs ahead of every callback subscribed before or after
    /// install.</para>
    /// <para><b>Draining.</b> Each update runs the items queued at the moment
    /// the drain starts; work posted during the drain itself waits for the
    /// next update, so a callback that re-posts itself cannot hang the
    /// Editor inside a single update. A posted item that throws is logged
    /// and skipped — one bad item must not starve the rest of the update.
    /// </para>
    /// <para><b>Lifecycle.</b> <see cref="Install"/> is invoked once per
    /// domain by <see cref="OpWeaveAdapterBootstrap"/> (skipped in asset
    /// import worker processes, SPEC §G1 item 3) and is idempotent. A domain
    /// reload drops still-queued work together with every other managed
    /// object.</para>
    /// </remarks>
    public static class MainThreadDispatcher
    {
        private static readonly ConcurrentQueue<Action> Pending = new ConcurrentQueue<Action>();
        private static int _installed;

        // The managed thread id recorded by the first Install call — the
        // Editor main thread by that method's contract. Negative until
        // installed, so IsMainThread fails closed for callers that cannot
        // prove they ran after domain-load initialization.
        private static int _mainThreadId = -1;

        /// <summary>
        /// Whether the calling thread is the Editor main thread recorded at
        /// <see cref="Install"/>. <c>false</c> while uninstalled: an
        /// unrecorded main thread is unprovable, so every consumer that
        /// requires it — the SPEC §D8 Dry Run gate — must refuse.
        /// </summary>
        internal static bool IsMainThread =>
            _mainThreadId >= 0 &&
            Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// Queues <paramref name="work"/> to run on the main thread at the
        /// head of a subsequent <see cref="EditorApplication.update"/>. Safe
        /// to call from any thread; the call itself touches no Unity API.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        public static void Post(Action work)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }
            Pending.Enqueue(work);
        }

        /// <summary>
        /// Subscribes the drain callback at the head of
        /// <see cref="EditorApplication.update"/>. Idempotent; must be called
        /// on the main thread.
        /// </summary>
        internal static void Install()
        {
            if (Interlocked.Exchange(ref _installed, 1) != 0)
            {
                return;
            }
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EditorApplication.update =
                new EditorApplication.CallbackFunction(Drain) + EditorApplication.update;
        }

        /// <summary>
        /// Runs the items queued at the moment the drain starts. Internal so
        /// the EditMode test assembly can exercise draining without invoking
        /// the <see cref="EditorApplication.update"/> delegate itself —
        /// raising that event inside a test re-enters the Unity Test
        /// Framework's own update callback and corrupts the run.
        /// </summary>
        internal static void Drain()
        {
            var budget = Pending.Count;
            while (budget-- > 0 && Pending.TryDequeue(out var work))
            {
                try
                {
                    work();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }
}
