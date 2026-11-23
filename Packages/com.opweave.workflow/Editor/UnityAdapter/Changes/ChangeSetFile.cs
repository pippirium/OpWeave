#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §F7 <c>changes.json</c> document of a Run directory: the Run's
    /// complete change list, written once when the Run ends (§F1 —
    /// "F7 の変更一覧（Run 終了時に確定）").
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each element keeps the exact §F7 shape the journal's
    /// <c>changes</c> members use —
    /// <c>{step, attempt, nodeId, action, target: {kind, path, guid?, globalId?}, undoable}</c>
    /// with the action in §0.4 snake_case — and <c>guid</c>/<c>globalId</c> are
    /// omitted rather than written null, because §A0.1 keeps an absent
    /// identifier distinct from a null one. The document is the single Canonical
    /// JSON serialization (§0.6) of that array, so the finalized file is
    /// byte-stable for the same change list.
    /// </para>
    /// <para>
    /// <b>The write.</b> Like every other file this adapter owns, the document
    /// goes to a same-directory temp file and then replaces the slot, so a write
    /// that fails halfway never leaves a truncated change list where the empty
    /// <c>[]</c> slot the Run Store created at acceptance still reads correctly.
    /// </para>
    /// </remarks>
    internal static class ChangeSetFile
    {
        internal const string FileName = "changes.json";

        private const string RunsDirectoryName = "Runs";
        private const string TempSuffix = ".opweave-tmp";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// The <c>changes.json</c> path of Run <paramref name="runId"/> under the
        /// SPEC §F1 layout — <c>&lt;root&gt;/Runs/&lt;runId&gt;/changes.json</c>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="runStoreRoot"/> or <paramref name="runId"/> is null or
        /// empty.
        /// </exception>
        internal static string PathOf(string runStoreRoot, string runId)
        {
            if (string.IsNullOrEmpty(runStoreRoot))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.",
                    nameof(runStoreRoot));
            }
            if (string.IsNullOrEmpty(runId))
            {
                throw new ArgumentException(
                    "A Run change list must carry its Run id.", nameof(runId));
            }
            return Path.Combine(runStoreRoot, RunsDirectoryName, runId, FileName);
        }

        /// <summary>
        /// The §F7 array of <paramref name="records"/> in canonical form — the
        /// document <see cref="Write"/> persists.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="records"/> is null.
        /// </exception>
        internal static string ToJson(IReadOnlyList<ChangeRecord> records)
        {
            if (records is null)
            {
                throw new ArgumentNullException(nameof(records));
            }
            return CanonicalJson.Serialize(ChangeRecordJson.Write(records));
        }

        /// <summary>
        /// Writes the finalized list to <paramref name="path"/>, replacing the
        /// empty slot <c>CreateRun</c> created.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="records"/> is null.</exception>
        /// <exception cref="IOException">
        /// The document could not be written or the temp file could not replace
        /// the slot — the caller reports the Run whose change list stayed
        /// unfinalized.
        /// </exception>
        internal static void Write(string path, IReadOnlyList<ChangeRecord> records)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException(
                    "The Run change list needs the file path to finalize into.",
                    nameof(path));
            }
            var bytes = Utf8.GetBytes(ToJson(records));
            var temp = path + TempSuffix;
            try
            {
                using (var stream = new FileStream(
                           temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // The same bounded retry the Run Store's ReplaceAtomically
                // applies (SPEC §F3 item 2, FileRunStore): a transient Win32
                // sharing failure of the replace step — an indexer or virus
                // scanner briefly holding the slot or the fresh temp file —
                // is retried a bounded number of times before it is a real
                // write failure.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Replace(temp, path, null);
                        }
                        else
                        {
                            File.Move(temp, path);
                        }
                        return;
                    }
                    catch (Exception e) when (
                        IsTransientReplaceFailure(e) && attempt < MaxReplaceAttempts)
                    {
                        Thread.Sleep(ReplaceRetryDelayMilliseconds);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // The temp file of a failed attempt is left where it is: deleting
                // it could fail the same way, and a stale temp never reads as a
                // change list — only changes.json does.
                throw new IOException(
                    "The Run change list could not be finalized.", e);
            }
        }

        private const int MaxReplaceAttempts = 5;
        private const int ReplaceRetryDelayMilliseconds = 15;

        // The transient Win32 codes the replace step surfaces while an
        // external reader holds a file — the same set FileRunStore retries:
        // ERROR_ACCESS_DENIED (5), ERROR_SHARING_VIOLATION (32) and
        // ERROR_UNABLE_TO_REMOVE_REPLACED (1175).
        private static bool IsTransientReplaceFailure(Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException))
            {
                return false;
            }
            var code = e.HResult & 0xFFFF;
            return code == 5 || code == 32 || code == 1175;
        }

    }
}
