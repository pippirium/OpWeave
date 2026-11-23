#nullable enable

using System;
using System.Globalization;
using System.IO;
using OpWeave.Core;
using OpWeave.Core.Persistence;
using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §E2 item 3 reload hand-off of the <c>coordinator.lock</c>
    /// exclusion. The OS handle that holds the exclusion outlives a domain
    /// reload (T-W1.7-04's L-14 observation: on Windows it is a raw
    /// <c>CreateFileW</c> handle no managed object owns), but the managed
    /// <see cref="CoordinatorRunLock"/> that owns it does not — so the value
    /// <see cref="CoordinatorRunLock.LockHandleValue"/> is kept in
    /// <see cref="SessionState"/>, which lives exactly as long as the Editor
    /// process, and the recovery pass of SPEC §F4 item 1 re-attaches it with
    /// <see cref="CoordinatorRunLock.TryAdopt"/> before it considers a fresh
    /// <see cref="CoordinatorRunLock.TryAcquire"/>. That order is what
    /// "リロード後は、排他を保持していることと…検証してから再開する" means in code:
    /// confirmation first, acquisition only when the confirmation failed — a
    /// failed confirmation whose acquisition then also fails is the SPEC §E2
    /// item 3 (a) case, and the pass must not resume anything.
    /// </summary>
    /// <remarks>
    /// <para><b>The recorded value is never proof.</b>
    /// <see cref="CoordinatorRunLock.TryAdopt"/> only succeeds when the OS proves
    /// the value is this process's live exclusive holder of this Run Store's
    /// <c>coordinator.lock</c>, so a stale value left by a released Run — or by a
    /// Run-acceptance probe that dropped its handle immediately — fails closed and
    /// the caller falls back to acquisition. Neither the handle value nor the
    /// recorded owner Run id is ever treated as proof of ownership (AGENTS
    /// invariant); both only tell the next domain where to ask the OS.</para>
    /// <para><b>Scope.</b> Both records belong to the normalized absolute Run
    /// Store root that wrote them: the SessionState key carries that root, and the
    /// value repeats it so a record written under an unexpected key still fails
    /// closed rather than reading as this root's. That isolation is not a detail —
    /// <see cref="SessionState"/> is shared by every host in the Editor process,
    /// and more than one root always exists there: <c>[InitializeOnLoad]</c>
    /// installs the production host over <c>Library/OpWeave/</c>, and the first
    /// delayCall of every domain runs its §F4 pass — before a second root's host
    /// resumes from the reload that compile caused. Records that shared one key
    /// let that pass spend another root's handle value, which silently turns SPEC
    /// §E2 item 3's confirmation into a lost exclusion — and leaves the previous
    /// domain's OS handle unrecoverable, because nothing else remembers its value.
    /// Like <see cref="EditorSessionId"/>, everything here is Editor-process state
    /// and disappears with the process — which is exactly why an Editor restart
    /// takes the acquisition path and a domain reload takes the adoption path.</para>
    /// </remarks>
    internal static class RunLockHandoff
    {
        /// <summary>The <c>OpWeave.*</c> SessionState key prefix of the handle
        /// record; <see cref="HandleKey"/> scopes it to the root it belongs to.</summary>
        internal const string HandleSessionStateKey = "OpWeave.RunLockHandle";

        /// <summary>
        /// The <c>OpWeave.*</c> SessionState key prefix of the record naming which
        /// Run this Editor last held the exclusion for — SPEC §E2 item 3 (a)'s
        /// "自分の Run", which the reload otherwise leaves unnamed.
        /// </summary>
        internal const string OwnedRunSessionStateKey = "OpWeave.RunLockOwnedRunId";

        /// <summary>
        /// Confirms the handle the previous domain of this Editor process held, or
        /// acquires the exclusion fresh (SPEC §E2 item 3's reload and restart paths,
        /// §F4 item 1's "保持していなければ取得を試み"). A failure means a live owner
        /// holds it: the caller resumes nothing and never touches a Run the owner
        /// may be mutating.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="runStoreRoot"/> is null or empty.
        /// </exception>
        internal static Result<CoordinatorRunLock> TryObtain(string runStoreRoot)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.", nameof(runStoreRoot));
            }
            var root = Path.GetFullPath(runStoreRoot);
            var persisted = Read(HandleKey(root), root);
            if (persisted is { } text && long.TryParse(text, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var value))
            {
                var adopted = CoordinatorRunLock.TryAdopt(root, new IntPtr(value));
                if (adopted.Ok && adopted.Value is { } held)
                {
                    // The same handle survives the reload; keep it recorded so the
                    // next domain can confirm it too.
                    RecordHandle(root, held.LockHandleValue);
                    return adopted;
                }
            }
            var acquired = CoordinatorRunLock.TryAcquire(root);
            if (acquired.Ok && acquired.Value is { } fresh)
            {
                RecordHandle(root, fresh.LockHandleValue);
            }
            return acquired;
        }

        /// <summary>
        /// Records the handle of an exclusion this domain just obtained, so a
        /// domain reload can confirm it instead of assuming the exclusion was lost
        /// (SPEC §E2 item 3: the exclusion is held per process and is not released
        /// by a reload).
        /// </summary>
        internal static void RecordHandle(string runStoreRoot, IntPtr lockHandleValue)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.", nameof(runStoreRoot));
            }
            var root = Path.GetFullPath(runStoreRoot);
            SessionState.SetString(
                HandleKey(root),
                root + "\n" +
                lockHandleValue.ToInt64().ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Records the Run this Editor holds the exclusion for — journaled at each
        /// lock-holding state change, cleared when the Run ends.
        /// </summary>
        internal static void RecordOwnedRun(string runStoreRoot, string runId)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.", nameof(runStoreRoot));
            }
            if (string.IsNullOrEmpty(runId))
            {
                throw new ArgumentException(
                    "The Run id must not be null or empty.", nameof(runId));
            }
            var root = Path.GetFullPath(runStoreRoot);
            SessionState.SetString(OwnedRunKey(root), root + "\n" + runId);
        }

        /// <summary>
        /// The Run this Editor process last held the exclusion for, when this root
        /// recorded one; null when nothing was recorded or another root recorded it.
        /// </summary>
        internal static string? OwnedRunId(string runStoreRoot)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                return null;
            }
            var root = Path.GetFullPath(runStoreRoot);
            return Read(OwnedRunKey(root), root);
        }

        /// <summary>
        /// Drops both records — the orderly Run end, where the exclusion is released
        /// and the next domain has nothing to confirm. A record left behind by an
        /// abnormal end stays exactly like the <c>lock.json</c> residue it pairs
        /// with: <see cref="CoordinatorRunLock.TryAdopt"/> fails closed on it.
        /// </summary>
        internal static void Clear(string runStoreRoot)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                return;
            }
            var root = Path.GetFullPath(runStoreRoot);
            if (Read(HandleKey(root), root) is not null)
            {
                SessionState.SetString(HandleKey(root), "");
            }
            if (Read(OwnedRunKey(root), root) is not null)
            {
                SessionState.SetString(OwnedRunKey(root), "");
            }
        }

        /// <summary>
        /// Whether this Editor's lock method can prove that the process a
        /// <c>lock.json</c> owner record belongs to has ended — the SPEC §E2 item 3
        /// (b) question. SPEC §E2 makes the answer conditional on the holding method
        /// ("上の維持方式が成立している場合"): on Windows the exclusion is a
        /// share-mode-0 handle the OS denies to every other opener, so a grant is
        /// only possible once the holder is gone, and the two-process contract tests
        /// of SPEC §T.4 L-14 prove it (T-W1.7-04/-05). Off Windows the equivalent
        /// proof is still T-W1.7-06's scope, so this answers
        /// <see langword="false"/>: the takeover of a recorded owner then fails
        /// closed with OW-RUN-002 and the record stays, exactly as item 3 (b)
        /// prescribes for a process that cannot confirm it.
        /// </summary>
        internal static bool PreviousOwnerTerminationProven() =>
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        // The SessionState key of one Run Store root's record. SessionState is one
        // flat namespace shared by every host in the Editor process, so the root is
        // part of the key: that is what keeps another root's record — the production
        // Editor's, or a second probe's — from replacing, reading or clearing this
        // one.
        private static string HandleKey(string normalizedRoot) =>
            HandleSessionStateKey + ":" + normalizedRoot;

        private static string OwnedRunKey(string normalizedRoot) =>
            OwnedRunSessionStateKey + ":" + normalizedRoot;

        // The record for one Run Store root: the value is stored as
        // "<normalized root>\n<value>" so a record written for another root — a
        // fixture's probe store, for instance — is never read as this Editor's, even
        // under a key that was not scoped as expected.
        private static string? Read(string key, string runStoreRoot)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                return null;
            }
            var stored = SessionState.GetString(key, "");
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }
            var separator = stored.IndexOf('\n');
            if (separator <= 0)
            {
                return null;
            }
            var root = stored.Substring(0, separator);
            var value = stored.Substring(separator + 1);
            if (!string.Equals(root, Path.GetFullPath(runStoreRoot), StringComparison.Ordinal) ||
                value.Length == 0)
            {
                return null;
            }
            return value;
        }
    }
}
