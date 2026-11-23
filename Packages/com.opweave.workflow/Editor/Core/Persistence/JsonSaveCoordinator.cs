#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The single cross-process write coordination for OpWeave-controlled
    /// <c>*.opweave.json</c> files (SPEC §H7 item 3, §0.6 SG-17): a per-path
    /// OS exclusion, a project-root ownership check and the crash-recovery
    /// intent records of the JSON sync.
    /// </summary>
    /// <remarks>
    /// <para><b>Lock.</b> For each resolved target path P the exclusion is an
    /// OS file handle on
    /// <c>Library/OpWeave/locks/json-&lt;sha256(normalized P)&gt;.lock</c>,
    /// held open for the whole read–decide–write critical section — a
    /// <c>CreateFileW</c> handle with share mode 0, the same mechanism and
    /// semantics as <see cref="CoordinatorRunLock"/>: a live owner's lock is
    /// never stolen, the lock file is never replaced or deleted while held,
    /// and the OS releases the handle on process death. v1 supports the
    /// Windows Editor only (SPEC §E2 item 3, decision D): the cross-process
    /// exclusion is unproven on every other platform, so acquisition there
    /// fails closed before the lock file is opened — never Busy, never
    /// retried. On Windows a contended lock is retried for up to
    /// <see cref="DefaultAcquireTimeoutMilliseconds"/> (2 s), then reports
    /// <see cref="AcquireStatus.Busy"/>.</para>
    /// <para><b>Ownership.</b> A path is only ever locked or written after it
    /// resolves inside the coordinator's project root — with every symlink,
    /// junction and reparse point on the deepest existing ancestor resolved
    /// (<c>GetFinalPathNameByHandleW</c> on Windows, <c>realpath(3)</c> on
    /// Unix), so a path that escapes the project through a reparse point is
    /// refused before a lock is taken or a byte is written.</para>
    /// <para><b>Intent records.</b> Every publishing write follows
    /// intent → publish → persist → clear: before the JSON bytes are
    /// published, an intent record is written atomically to
    /// <c>Library/OpWeave/jsonsync/graph-&lt;GUID&gt;.intent.json</c>
    /// naming the target path, the graph GUID, the write kind
    /// (<c>create</c>/<c>update</c>), the disk revision the write was bound
    /// to (<c>base</c>, lowercase-hex SHA-256 or <c>absent</c>) and the
    /// output's byte hash and GUI-owned projection hash. A later run
    /// reconciles a leftover record against the current file: publish done
    /// but persist lost re-persists (or asks the human when the graph's own
    /// projection moved), publish not reached clears the stale record.
    /// A record that does not parse, names another graph or another path, or
    /// carries a malformed hash is reported once and never acted upon.</para>
    /// <para><b>Durability.</b> An intent record and a create-only
    /// publication both land the same way: a same-directory temp file is
    /// written and fsync'd (<see cref="FileStream.Flush(bool)"/>), then a
    /// durable rename publishes it (<see cref="DurableRename"/> for the
    /// intent's replace, a no-overwrite move for
    /// <see cref="PublishCreateAtomic"/>) — a flush failure is a write
    /// failure, never a silent durable claim, and a Windows sharing
    /// violation or access-denied — a reader, indexer or antivirus
    /// holding the record open — on the intent's replace is retried a
    /// bounded number of times before the write fails closed.</para>
    /// </remarks>
    internal sealed class JsonSaveCoordinator
    {
        /// <summary>The bounded acquisition wait before <see cref="AcquireStatus.Busy"/> is reported.</summary>
        internal const int DefaultAcquireTimeoutMilliseconds = 2000;

        /// <summary>The bounded number of publish attempts on a Windows sharing violation before the intent write fails closed.</summary>
        internal const int MaxPublishAttempts = 5;

        /// <summary>The intent record kind for a first-create publication.</summary>
        internal const string IntentKindCreate = "create";

        /// <summary>The intent record kind for an update publication over an existing file.</summary>
        internal const string IntentKindUpdate = "update";

        /// <summary>The intent <c>base</c> value when the write expected no file at all.</summary>
        internal const string IntentBaseAbsent = "absent";

        private const int PollDelayMilliseconds = 25;
        private const int PublishRetryDelayMilliseconds = 15;
        private const int MaxPathDepth = 64;

        private static readonly bool s_isWindows =
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        private readonly StringComparison _comparison;
        private readonly string _resolvedProjectRoot;

        /// <exception cref="ArgumentException"><paramref name="projectRoot"/> is null or empty.</exception>
        internal JsonSaveCoordinator(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                throw new ArgumentException(
                    "The project root path must not be null or empty.", nameof(projectRoot));
            }
            _comparison = s_isWindows ? StringComparison.OrdinalIgnoreCase
                                      : StringComparison.Ordinal;
            _resolvedProjectRoot = TrimTrailingSeparator(
                ResolveFinalPath(Path.GetFullPath(projectRoot)) ??
                    Path.GetFullPath(projectRoot));
        }

        // Internal test seams — production callers leave them null. The
        // flush seam replaces the fsync step of the durable temp write so a
        // regression test can fail it deterministically; the replace seam
        // replaces the intent record's durable-rename publish so the bounded
        // sharing-violation retry can be driven without timing.
        internal static Action<FileStream>? FlushToDiskForTesting { get; set; }
        internal static Func<string, string, IOException?>? PublishReplaceForTesting { get; set; }

        /// <summary>The project root with its own reparse points resolved.</summary>
        internal string ResolvedProjectRoot => _resolvedProjectRoot;

        /// <summary>The per-path lock files' directory under the project.</summary>
        internal string LocksDirectory => Path.Combine(
            _resolvedProjectRoot, "Library", "OpWeave", "locks");

        /// <summary>The crash-recovery intent records' directory under the project.</summary>
        internal string IntentDirectory => Path.Combine(
            _resolvedProjectRoot, "Library", "OpWeave", "jsonsync");

        /// <summary>
        /// Resolves <paramref name="path"/> — absolute or project-relative —
        /// to its canonical absolute form and proves it lives inside the
        /// project root after every symlink, junction and reparse point on
        /// the deepest existing ancestor is resolved. A path that escapes
        /// the project is <see cref="PathResolveStatus.Refused"/>; this is
        /// the guard every OpWeave JSON write passes before a lock is taken
        /// or a byte is written.
        /// </summary>
        internal PathResolveResult ResolveOwnedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new PathResolveResult(
                    PathResolveStatus.Invalid, null, "the workflow path is empty");
            }
            string full;
            try
            {
                full = Path.IsPathRooted(path)
                    ? Path.GetFullPath(path)
                    : Path.GetFullPath(Path.Combine(_resolvedProjectRoot, path));
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return new PathResolveResult(
                    PathResolveStatus.Invalid, null,
                    "the workflow path is not valid");
            }
            var resolved = ResolveFinalPath(full);
            if (resolved is null)
            {
                return new PathResolveResult(
                    PathResolveStatus.IoError, null,
                    "the workflow path could not be resolved");
            }
            if (!IsUnderRoot(resolved))
            {
                return new PathResolveResult(
                    PathResolveStatus.Refused, null,
                    "the workflow path is outside the project");
            }
            return new PathResolveResult(PathResolveStatus.Resolved, resolved, null);
        }

        private bool IsUnderRoot(string resolved)
        {
            var root = _resolvedProjectRoot;
            return resolved.Length > root.Length &&
                resolved.StartsWith(root, _comparison) &&
                (resolved[root.Length] == Path.DirectorySeparatorChar ||
                    resolved[root.Length] == Path.AltDirectorySeparatorChar);
        }

        // The canonical absolute form of a possibly nonexistent path: the
        // deepest existing ancestor is resolved through the OS (reparse
        // points included) and the remaining segments are appended. Null
        // when the ancestor's resolution failed — the caller fails closed.
        private string? ResolveFinalPath(string fullPath)
        {
            var candidate = TrimTrailingSeparator(fullPath);
            for (var depth = 0; depth < MaxPathDepth && candidate.Length != 0; depth++)
            {
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    var resolvedAncestor = ResolveExisting(candidate);
                    if (resolvedAncestor is null)
                    {
                        return null;
                    }
                    var remainder = fullPath.Length > candidate.Length
                        ? fullPath.Substring(candidate.Length).TrimStart(
                            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        : string.Empty;
                    return remainder.Length == 0
                        ? resolvedAncestor
                        : Path.Combine(resolvedAncestor, remainder);
                }
                candidate = Path.GetDirectoryName(candidate) ?? string.Empty;
            }
            return null;
        }

        // The OS-resolved final path of an existing file or directory —
        // every reparse point on it resolved.
        private static string? ResolveExisting(string path)
        {
            if (s_isWindows)
            {
                var handle = WindowsNative.OpenForFinalPath(path);
                if (handle is null || handle.IsInvalid)
                {
                    return null;
                }
                try
                {
                    return WindowsNative.GetFinalPathName(handle);
                }
                finally
                {
                    handle.Dispose();
                }
            }
            return UnixNative.RealPath(path);
        }

        /// <summary>
        /// The lock file for a resolved target path:
        /// <c>Library/OpWeave/locks/json-&lt;sha256(normalized P)&gt;.lock</c>.
        /// The hash input is the OS-resolved path with forward slashes and
        /// case-folded where the platform's paths compare case-insensitively,
        /// so every process addressing the same file derives the same lock.
        /// </summary>
        internal string LockPathFor(string resolvedPath)
        {
            var normalized = resolvedPath.Replace('\\', '/');
            if (s_isWindows)
            {
                normalized = normalized.ToLowerInvariant();
            }
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }
            return Path.Combine(
                LocksDirectory, "json-" + Sha256Hex(s_utf8.GetBytes(normalized)) + ".lock");
        }

        /// <summary>
        /// Acquires the per-path OS exclusion for <paramref name="resolvedPath"/>
        /// — a path <see cref="ResolveOwnedPath"/> already resolved and proven
        /// inside the project. Retries a live owner's lock for up to
        /// <paramref name="timeoutMilliseconds"/>, then reports
        /// <see cref="AcquireStatus.Busy"/>. The returned
        /// <see cref="JsonSaveLock"/> holds the handle until disposed; the
        /// lock file itself is never replaced or deleted. v1 supports the
        /// Windows Editor only (SPEC §E2 item 3, decision D): off Windows the
        /// exclusion is unproven, so the lock fails closed before the file is
        /// opened — never Busy, never retried.
        /// </summary>
        internal AcquireResult TryAcquire(string resolvedPath) =>
            TryAcquire(resolvedPath, DefaultAcquireTimeoutMilliseconds);

        internal AcquireResult TryAcquire(string resolvedPath, int timeoutMilliseconds)
        {
            var lockPath = LockPathFor(resolvedPath);
            try
            {
                Directory.CreateDirectory(LocksDirectory);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return AcquireResult.IoError(
                    lockPath, "the JSON sync lock directory could not be created");
            }
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                var attempt = TryOpenExclusive(lockPath);
                switch (attempt.Status)
                {
                    case AcquireStatus.Acquired:
                        return AcquireResult.Acquired(attempt.Lock!, lockPath);
                    case AcquireStatus.Busy:
                        if (deadline.ElapsedMilliseconds >= timeoutMilliseconds)
                        {
                            return AcquireResult.Busy(lockPath);
                        }
                        Thread.Sleep(PollDelayMilliseconds);
                        continue;
                    default:
                        return AcquireResult.IoError(lockPath, attempt.ErrorMessage);
                }
            }
        }

        // One acquisition attempt: the OS exclusion on the lock file — a
        // share-mode-0 CreateFileW on the v1-supported Windows Editor. The
        // cross-process exclusion is unproven everywhere else (SPEC §E2 item
        // 3, decision D), so the lock fails closed before the file is ever
        // opened — the same unobtainable cause CoordinatorRunLock reports,
        // never Busy, never retried. OPEN_ALWAYS creates the file when
        // missing; it is never truncated, replaced or deleted.
        private static Attempt TryOpenExclusive(string lockPath)
        {
            if (!CoordinatorRunLock.IsWindowsPlatform)
            {
                return Attempt.Failed(CoordinatorRunLock.NonWindowsExclusionCause);
            }
            var error = 0;
            var handle = WindowsNative.OpenExclusive(lockPath, out error);
            if (handle is not null && !handle.IsInvalid)
            {
                return Attempt.Held(new JsonSaveLock(lockPath, handle));
            }
            return error == WindowsNative.ErrorSharingViolation ||
                error == WindowsNative.ErrorLockViolation
                ? Attempt.Contended()
                : Attempt.Failed(
                    "the JSON sync lock file could not be opened (Win32 error " +
                    error.ToString(CultureInfo.InvariantCulture) +
                    ")");
        }

        // ── Intent records (SPEC §H7 item 3 crash recovery) ────────────

        /// <summary>
        /// The intent record path for a validated
        /// <paramref name="graphGuid"/>: callers reject anything that is
        /// not exactly 32 lowercase hex characters first
        /// (<see cref="IsGraphGuid"/>), so a separator or <c>..</c> can
        /// never escape the jsonsync directory.
        /// </summary>
        internal string IntentPathFor(string graphGuid) => Path.Combine(
            IntentDirectory, "graph-" + graphGuid + ".intent.json");

        /// <summary>
        /// Reads and validates the pending intent record of the graph whose
        /// asset GUID is <paramref name="graphGuid"/>: the record must parse,
        /// name this graph, carry every required member with well-formed
        /// hashes (lowercase 64-hex; <c>base</c> may instead be
        /// <c>absent</c>), name an allowed kind/base combination
        /// (<c>create</c> is bound to <c>absent</c>, <c>update</c> to a
        /// disk-revision hash) and point at either the graph's recorded
        /// <paramref name="recordedPath"/> or — only for an unlinked first
        /// link, when <paramref name="recordedPath"/> is empty — the
        /// deterministic sibling <paramref name="siblingPath"/>.
        /// An absent record reports <see cref="IntentReadState.Absent"/>; a
        /// record failing any check reports
        /// <see cref="IntentReadState.Invalid"/> with a fixed English
        /// warning — it is left untouched and never acted upon.
        /// <see cref="IntentReadResult.RecordHash"/> snapshots the record's
        /// raw bytes for <see cref="ClearIntentIfUnchanged"/>.
        /// </summary>
        internal IntentReadResult ReadIntent(
            string graphGuid, string recordedPath, string? siblingPath)
        {
            if (!IsGraphGuid(graphGuid))
            {
                return IntentReadResult.Invalid(
                    "the graph GUID is not a 32-character lowercase hex string; the intent record is left untouched",
                    null);
            }
            var intentPath = IntentPathFor(graphGuid);
            byte[] raw;
            try
            {
                if (!File.Exists(intentPath))
                {
                    return IntentReadResult.Absent;
                }
                raw = File.ReadAllBytes(intentPath);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return IntentReadResult.Invalid(
                    "the JSON sync intent record could not be read; it is left untouched",
                    null);
            }
            var recordHash = Sha256Hex(raw);
            var parsed = CanonicalJson.Parse(s_utf8.GetString(raw));
            if (!parsed.Ok || parsed.Value is not JObject root)
            {
                return IntentReadResult.Invalid(
                    "the JSON sync intent record does not parse; it is left untouched",
                    recordHash);
            }
            var path = StringMember(root, "path");
            var guid = StringMember(root, "graphGuid");
            var kind = StringMember(root, "kind");
            var baseHash = StringMember(root, "base");
            var outputHash = StringMember(root, "outputHash");
            var outputGuiHash = StringMember(root, "outputGuiHash");
            if (path is null || guid is null || kind is null || baseHash is null ||
                outputHash is null || outputGuiHash is null)
            {
                return IntentReadResult.Invalid(
                    "the JSON sync intent record is missing required members; it is left untouched",
                    recordHash);
            }
            if (!string.Equals(guid, graphGuid, StringComparison.Ordinal))
            {
                return IntentReadResult.Invalid(
                    "the JSON sync intent record names a different graph; it is left untouched",
                    recordHash);
            }
            if (kind != IntentKindCreate && kind != IntentKindUpdate)
            {
                return IntentReadResult.Invalid(
                    "the JSON sync intent record names an unknown write kind; it is left untouched",
                    recordHash);
            }
            // Every hash the record carries is a lowercase-64-hex SHA-256 —
            // except "base", which may instead be the literal "absent" of a
            // first create, and then only on kind "create" (an update is
            // bound to a real disk revision; a create cannot be).
            if (!IsHex64(outputHash) || !IsHex64(outputGuiHash) ||
                (baseHash != IntentBaseAbsent && !IsHex64(baseHash)))
            {
                return IntentReadResult.Invalid(
                    "the JSON sync intent record carries a malformed hash; it is left untouched",
                    recordHash);
            }
            if ((kind == IntentKindCreate) != (baseHash == IntentBaseAbsent))
            {
                return IntentReadResult.Invalid(
                    "the JSON sync intent record's kind does not match its base revision; it is left untouched",
                    recordHash);
            }
            var owned = ResolveOwnedPath(path);
            if (owned.Status != PathResolveStatus.Resolved)
            {
                return IntentReadResult.Invalid(
                    "the JSON sync intent record's path does not resolve inside the project; it is left untouched",
                    recordHash);
            }
            // The deterministic sibling path is accepted only for an
            // unlinked first link — an empty recordedPath. A graph already
            // linked to a JSON accepts the recorded path only, so a stale
            // record can never pull the sibling into the graph's link.
            var matchesRecorded = recordedPath.Length != 0 &&
                ResolvesTo(recordedPath, owned.ResolvedPath!);
            var matchesSibling = recordedPath.Length == 0 && siblingPath is not null &&
                ResolvesTo(siblingPath, owned.ResolvedPath!);
            if (!matchesRecorded && !matchesSibling)
            {
                return IntentReadResult.Invalid(
                    "the JSON sync intent record's path matches neither the recorded link nor the sibling path; it is left untouched",
                    recordHash);
            }
            return IntentReadResult.Valid(new JsonSyncIntent(
                owned.ResolvedPath!, guid, kind, baseHash, outputHash, outputGuiHash),
                recordHash);
        }

        private bool ResolvesTo(string candidate, string resolvedExpected)
        {
            var resolved = ResolveOwnedPath(candidate);
            return resolved.Status == PathResolveStatus.Resolved &&
                string.Equals(resolved.ResolvedPath, resolvedExpected, _comparison);
        }

        // A lowercase-64-hex SHA-256 rendering — the only hash format the
        // intent record accepts; uppercase or shortened forms are malformed.
        private static bool IsHex64(string value) => IsLowerHex(value, 64);

        // The graph asset GUID the intent file name is keyed on: exactly 32
        // lowercase hex characters — the form the .meta GUID takes. Anything
        // else is rejected before a path is built so a separator or ".."
        // can never escape the jsonsync directory.
        private static bool IsGraphGuid(string graphGuid) =>
            IsLowerHex(graphGuid, 32);

        private static bool IsLowerHex(string value, int length)
        {
            if (value is null || value.Length != length)
            {
                return false;
            }
            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Writes <paramref name="intent"/> atomically and durably to its
        /// record path — the first step of every publishing write. The
        /// record lands under the graph's GUID so recovery finds it without
        /// knowing the target: a same-directory temp file is written and
        /// fsync'd, then the durable rename publishes it. A Windows sharing
        /// violation or access-denied — a reader, indexer or antivirus
        /// holding the record open — on the replace is retried up to
        /// <see cref="MaxPublishAttempts"/> times before the write fails
        /// closed; a flush failure is not Ok.
        /// </summary>
        internal (bool Ok, string? Error) WriteIntent(JsonSyncIntent intent)
        {
            if (!IsGraphGuid(intent.GraphGuid))
            {
                return (false,
                    "the graph GUID is not a 32-character lowercase hex string");
            }
            var root = new JObject
            {
                ["path"] = intent.Path,
                ["graphGuid"] = intent.GraphGuid,
                ["kind"] = intent.Kind,
                ["base"] = intent.BaseHash,
                ["outputHash"] = intent.OutputHash,
                ["outputGuiHash"] = intent.OutputGuiHash,
            };
            var bytes = s_utf8.GetBytes(CanonicalJson.Serialize(root));
            var intentPath = IntentPathFor(intent.GraphGuid);
            var tempPath = intentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(IntentDirectory);
                WriteTempDurable(tempPath, bytes);
                for (var attempt = 1; ; attempt++)
                {
                    var failure = PublishReplace(tempPath, intentPath);
                    if (failure is null)
                    {
                        return (true, null);
                    }
                    if (!IsSharingViolation(failure) || attempt >= MaxPublishAttempts)
                    {
                        return (false,
                            "the JSON sync intent record could not be published");
                    }
                    Thread.Sleep(PublishRetryDelayMilliseconds);
                }
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return (false, "the JSON sync intent record could not be written");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception e) when (IsIoFailure(e))
                {
                    // Best-effort cleanup; a leftover temp file does not
                    // change the write's outcome.
                }
            }
        }

        // The durable temp half of every atomic publish: the bytes land in
        // a same-directory temp file and are flushed to disk — Flush(true),
        // FlushFileBuffers/fsync under the hood — before the rename
        // publishes them. A flush failure surfaces to the caller as a write
        // failure, never a partial or silently non-durable record.
        private void WriteTempDurable(string tempPath, byte[] bytes)
        {
            using (var stream = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                if (FlushToDiskForTesting is { } hook)
                {
                    hook(stream);
                }
                else
                {
                    stream.Flush(true);
                }
            }
        }

        // The durable-rename publish of the intent record, behind the test
        // seam: MoveFileExW REPLACE_EXISTING|WRITE_THROUGH on Windows,
        // rename(2) plus a directory fsync elsewhere (DurableRename), so the
        // record appears atomically and the rename itself is durable where
        // the platform offers that.
        private static IOException? PublishReplace(string tempPath, string targetPath)
        {
            if (PublishReplaceForTesting is { } publish)
            {
                return publish(tempPath, targetPath);
            }
            try
            {
                DurableRename.Move(tempPath, targetPath);
                return null;
            }
            catch (IOException e)
            {
                return e;
            }
        }

        // The transient "another process still has the file open" class the
        // bounded retry absorbs: ERROR_SHARING_VIOLATION and
        // ERROR_LOCK_VIOLATION, plus the ERROR_ACCESS_DENIED MoveFileExW's
        // replace reports when another process — an indexer, antivirus or a
        // FileShare.Read reader — holds the *target* open (a held source
        // reports 32 instead). Every other failure is final, and off
        // Windows the classification never applies: POSIX has no share-mode
        // conflicts to wait out.
        private static bool IsSharingViolation(IOException e) =>
            s_isWindows &&
                ((e.HResult & 0xFFFF) == WindowsNative.ErrorSharingViolation ||
                    (e.HResult & 0xFFFF) == WindowsNative.ErrorLockViolation ||
                    (e.HResult & 0xFFFF) == WindowsNative.ErrorAccessDenied);

        /// <summary>
        /// Deletes the graph's intent record — the last step of a publishing
        /// write, and the recovery step for a record whose publish provably
        /// never happened.
        /// </summary>
        internal (bool Ok, string? Error) ClearIntent(string graphGuid)
        {
            if (!IsGraphGuid(graphGuid))
            {
                return (false,
                    "the graph GUID is not a 32-character lowercase hex string");
            }
            var intentPath = IntentPathFor(graphGuid);
            try
            {
                if (File.Exists(intentPath))
                {
                    File.Delete(intentPath);
                }
                return (true, null);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return (false, "the JSON sync intent record could not be cleared");
            }
        }

        /// <summary>
        /// The compare-and-clear half of intent re-read under the path lock:
        /// called while the caller holds the target path's
        /// <see cref="JsonSaveLock"/>, it deletes the graph's record only
        /// when the record's raw bytes still hash to
        /// <paramref name="expectedRecordHash"/> — the snapshot the caller
        /// took (for example <see cref="IntentReadResult.RecordHash"/>). A
        /// record that changed since that snapshot is never cleared and is
        /// reported as <c>Cleared=false</c>; a record already gone is the
        /// cleared state.
        /// </summary>
        internal (bool Ok, bool Cleared, string? Error) ClearIntentIfUnchanged(
            string graphGuid, string expectedRecordHash)
        {
            if (!IsGraphGuid(graphGuid))
            {
                return (false, false,
                    "the graph GUID is not a 32-character lowercase hex string");
            }
            var intentPath = IntentPathFor(graphGuid);
            try
            {
                if (!File.Exists(intentPath))
                {
                    return (true, true, null);
                }
                var current = Sha256Hex(File.ReadAllBytes(intentPath));
                if (!string.Equals(current, expectedRecordHash, StringComparison.Ordinal))
                {
                    return (true, false, null);
                }
                File.Delete(intentPath);
                return (true, true, null);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return (false, false, "the JSON sync intent record could not be cleared");
            }
        }

        // ── Create-only publication ─────────────────────────────────────

        /// <summary>
        /// The create-only publication SPEC §H7 item 3 requires for a new
        /// <c>*.opweave.json</c> — called on a path
        /// <see cref="ResolveOwnedPath"/> already resolved, inside the held
        /// path lock. The bytes land in a same-directory, fsync'd temp file
        /// and are moved over <paramref name="resolvedPath"/> with no
        /// overwrite: a destination that appeared first is reported
        /// <see cref="PublishCreateStatus.AlreadyExists"/>, never truncated,
        /// and an interrupted publish can only leave the temp file (removed
        /// best-effort), never a partial target. The directory is the
        /// caller's to create.
        /// </summary>
        internal PublishCreateResult PublishCreateAtomic(string resolvedPath, byte[] bytes)
        {
            var tempPath = resolvedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteTempDurable(tempPath, bytes);
                var failure = PublishCreateMove(tempPath, resolvedPath);
                if (failure is null)
                {
                    return PublishCreateResult.Created();
                }
                return IsAlreadyExists(failure)
                    ? PublishCreateResult.AlreadyExists()
                    : PublishCreateResult.IoError("the file could not be published");
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return PublishCreateResult.IoError("the file could not be published");
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (Exception e) when (IsIoFailure(e))
                {
                    // Best-effort cleanup; a leftover temp file is residue
                    // of an interrupted create, never a partial target.
                }
            }
        }

        // The no-overwrite publish: MoveFileExW with WRITE_THROUGH but no
        // REPLACE_EXISTING on Windows — an existing destination fails the
        // call instead of being replaced — and link(2)+unlink(2) elsewhere,
        // where rename(2) would silently replace an existing target.
        private static IOException? PublishCreateMove(string tempPath, string targetPath)
        {
            if (s_isWindows)
            {
                return WindowsNative.MoveFileNoReplace(tempPath, targetPath)
                    ? null
                    : NativeFailure("the file could not be published");
            }
            return UnixNative.LinkCreateOnly(tempPath, targetPath);
        }

        // ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS on Windows, EEXIST
        // elsewhere: the destination existed before the create-only move
        // landed — the caller maps this to "already present", not failure.
        private static bool IsAlreadyExists(IOException e)
        {
            var code = e.HResult & 0xFFFF;
            return code == WindowsNative.ErrorFileExists ||
                code == WindowsNative.ErrorAlreadyExists ||
                (!s_isWindows && code == UnixNative.ErrorExist);
        }

        /// <summary>Lowercase-hex SHA-256 of <paramref name="bytes"/>.</summary>
        internal static string Sha256Hex(byte[] bytes)
        {
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }
            var chars = new char[hash.Length * 2];
            const string digits = "0123456789abcdef";
            for (var i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = digits[hash[i] >> 4];
                chars[i * 2 + 1] = digits[hash[i] & 0xF];
            }
            return new string(chars);
        }

        /// <summary>UTF-8 without BOM — the encoding every OpWeave JSON write uses.</summary>
        internal static byte[] Utf8Bytes(string text) => s_utf8.GetBytes(text);

        private static string? StringMember(JObject root, string name) =>
            root[name] is JValue value && value.Type == JTokenType.String &&
                value.Value is string s && s.Length != 0
                ? s
                : null;

        private static string TrimTrailingSeparator(string path)
        {
            var end = path.Length;
            while (end > 0 &&
                (path[end - 1] == Path.DirectorySeparatorChar ||
                    path[end - 1] == Path.AltDirectorySeparatorChar) &&
                // A bare root ("C:\", "/") keeps its separator.
                end > Path.GetPathRoot(path)?.Length)
            {
                end--;
            }
            return end == path.Length ? path : path.Substring(0, end);
        }

        private static bool IsIoFailure(Exception e) =>
            e is IOException ||
            e is UnauthorizedAccessException ||
            e is NotSupportedException ||
            e is System.Security.SecurityException ||
            e is ArgumentException;

        // The IOException shape every native publish failure carries:
        // FACILITY_WIN32 with the native error in the low 16 bits, so the
        // sharing-violation and already-exists classification reads
        // HResult uniformly.
        private static IOException NativeFailure(string message)
        {
            var error = Marshal.GetLastWin32Error();
            return new IOException(
                message + " (error " +
                    error.ToString(CultureInfo.InvariantCulture) + ").",
                unchecked((int)0x80070000) | (error & 0xFFFF));
        }

        // kernel32 + libc entry points, reached only on their own platform.
        private static class WindowsNative
        {
            internal const int ErrorAccessDenied = 5;
            internal const int ErrorSharingViolation = 32;
            internal const int ErrorLockViolation = 33;
            internal const int ErrorFileExists = 80;
            internal const int ErrorAlreadyExists = 183;

            private const uint FileReadAttributes = 0x0080;
            private const uint GenericRead = 0x80000000;
            private const uint FileShareAll = 0x00000007;
            private const uint OpenExisting = 3;
            private const uint OpenAlways = 4;
            private const uint FileFlagBackupSemantics = 0x02000000;
            private const uint FileNameNormalized = 0x0;
            private const uint VolumeNameDos = 0x0;
            private const uint MoveFileWriteThrough = 0x00000008;

            // A share-mode-0 open — the Windows half of the OS exclusion:
            // every later open from any process is denied while the handle
            // lives, and the OS releases it on process death.
            internal static SafeFileHandle? OpenExclusive(string path, out int error)
            {
                var handle = CreateFileW(
                    path, GenericRead, 0, IntPtr.Zero, OpenAlways, 0, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    return null;
                }
                error = 0;
                return handle;
            }

            // A query-only open that resolves reparse points: files and
            // directories both open with FILE_FLAG_BACKUP_SEMANTICS and full
            // sharing so the probe itself never contends.
            internal static SafeFileHandle? OpenForFinalPath(string path)
            {
                var handle = CreateFileW(
                    path, FileReadAttributes, FileShareAll, IntPtr.Zero,
                    OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    return null;
                }
                return handle;
            }

            // The OS-resolved final path of an open handle — every junction,
            // symlink and reparse point resolved, returned as a normal DOS
            // path (the \\?\ prefix stripped).
            internal static string? GetFinalPathName(SafeFileHandle handle)
            {
                var builder = new StringBuilder(4096);
                var length = GetFinalPathNameByHandleW(
                    handle, builder, (uint)builder.Capacity,
                    FileNameNormalized | VolumeNameDos);
                if (length == 0 || length > builder.Capacity)
                {
                    return null;
                }
                var resolved = builder.ToString(0, (int)length);
                if (resolved.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal))
                {
                    return "\\\\" + resolved.Substring(8);
                }
                if (resolved.StartsWith("\\\\?\\", StringComparison.Ordinal))
                {
                    return resolved.Substring(4);
                }
                return resolved;
            }

            // The create-only publish move: MoveFileExW with
            // MOVEFILE_WRITE_THROUGH but without MOVEFILE_REPLACE_EXISTING —
            // durable where Windows offers durability, and an existing
            // destination fails the call rather than being replaced.
            internal static bool MoveFileNoReplace(string tempPath, string targetPath) =>
                MoveFileExW(tempPath, targetPath, MoveFileWriteThrough);

            [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool MoveFileExW(
                string lpExistingFileName, string lpNewFileName, uint dwFlags);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern SafeFileHandle CreateFileW(
                string lpFileName,
                uint dwDesiredAccess,
                uint dwShareMode,
                IntPtr lpSecurityAttributes,
                uint dwCreationDisposition,
                uint dwFlagsAndAttributes,
                IntPtr hTemplateFile);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern uint GetFinalPathNameByHandleW(
                SafeFileHandle hFile,
                [Out] StringBuilder lpszFilePath,
                uint cchFilePath,
                uint dwFlags);
        }

        private static class UnixNative
        {
            internal const int ErrorExist = 17;

            private const int ReadOnly = 0;

            // realpath(3) on the existing path — every symlink resolved.
            // NULL on failure; the caller fails closed.
            internal static string? RealPath(string path)
            {
                var resolved = realpath(path, IntPtr.Zero);
                if (resolved == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    return Marshal.PtrToStringUTF8(resolved);
                }
                finally
                {
                    free(resolved);
                }
            }

            // The create-only publish on POSIX: link(2) adds the committed
            // name atomically — EEXIST when a destination raced in — then
            // unlink(2) drops the temp name and a directory fsync makes the
            // new entry durable, the same shape DurableRename.MovePosix
            // gives a replace. rename(2) is not used: it would silently
            // replace an existing target.
            internal static IOException? LinkCreateOnly(string tempPath, string targetPath)
            {
                if (link(tempPath, targetPath) != 0)
                {
                    return LastErrno("the file could not be published");
                }
                // The target entry exists from here on: dropping the temp
                // name is best-effort cleanup the caller's finally retries;
                // its failure cannot uncommit the create.
                unlink(tempPath);
                var directory =
                    Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? ".";
                var descriptor = open(directory, ReadOnly, 0);
                if (descriptor < 0)
                {
                    return LastErrno("the containing directory could not be opened");
                }
                int closeResult;
                try
                {
                    if (fsync(descriptor) != 0)
                    {
                        return LastErrno("the containing directory could not be fsync'd");
                    }
                }
                finally
                {
                    closeResult = close(descriptor);
                }
                return closeResult != 0
                    ? LastErrno("the directory handle could not be closed")
                    : null;
            }

            private static IOException LastErrno(string message)
            {
                var error = Marshal.GetLastWin32Error();
                return new IOException(
                    message + " (error " +
                        error.ToString(CultureInfo.InvariantCulture) + ").",
                    unchecked((int)0x80070000) | (error & 0xFFFF));
            }

            [DllImport("libc", SetLastError = true)]
            private static extern IntPtr realpath(string pathname, IntPtr resolvedPath);

            [DllImport("libc")]
            private static extern void free(IntPtr ptr);

            [DllImport("libc", EntryPoint = "link", SetLastError = true)]
            private static extern int link(string oldpath, string newpath);

            [DllImport("libc", EntryPoint = "unlink", SetLastError = true)]
            private static extern int unlink(string pathname);

            [DllImport("libc", EntryPoint = "open", SetLastError = true)]
            private static extern int open(string pathname, int flags, int mode);

            [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
            private static extern int fsync(int fd);

            [DllImport("libc", EntryPoint = "close", SetLastError = true)]
            private static extern int close(int fd);
        }

        private sealed class Attempt
        {
            private Attempt(AcquireStatus status, JsonSaveLock? held, string? errorMessage)
            {
                Status = status;
                Lock = held;
                ErrorMessage = errorMessage;
            }

            internal AcquireStatus Status { get; }
            internal JsonSaveLock? Lock { get; }
            internal string? ErrorMessage { get; }

            internal static Attempt Held(JsonSaveLock held) =>
                new Attempt(AcquireStatus.Acquired, held, null);

            internal static Attempt Contended() =>
                new Attempt(AcquireStatus.Busy, null, null);

            internal static Attempt Failed(string? errorMessage) =>
                new Attempt(AcquireStatus.IoError, null, errorMessage);
        }
    }

    /// <summary>What a <see cref="JsonSaveCoordinator.TryAcquire"/> attempt produced.</summary>
    internal enum AcquireStatus
    {
        /// <summary>The OS exclusion is held; the lease is in <see cref="AcquireResult.Lock"/>.</summary>
        Acquired = 0,

        /// <summary>A live owner holds the lock; the bounded wait elapsed without acquiring it.</summary>
        Busy = 1,

        /// <summary>The lock file could not be opened or its directory created.</summary>
        IoError = 2,
    }

    /// <summary>The outcome of one <see cref="JsonSaveCoordinator.TryAcquire"/>.</summary>
    internal sealed class AcquireResult
    {
        private AcquireResult(
            AcquireStatus status, JsonSaveLock? held, string lockPath, string? errorMessage)
        {
            Status = status;
            Lock = held;
            LockPath = lockPath;
            ErrorMessage = errorMessage;
        }

        internal AcquireStatus Status { get; }

        /// <summary>The held exclusion on <see cref="AcquireStatus.Acquired"/>; dispose to release.</summary>
        internal JsonSaveLock? Lock { get; }

        /// <summary>The lock file this acquisition addressed.</summary>
        internal string LockPath { get; }

        internal string? ErrorMessage { get; }

        internal static AcquireResult Acquired(JsonSaveLock held, string lockPath) =>
            new AcquireResult(AcquireStatus.Acquired, held, lockPath, null);

        internal static AcquireResult Busy(string lockPath) =>
            new AcquireResult(AcquireStatus.Busy, null, lockPath, null);

        internal static AcquireResult IoError(string lockPath, string? errorMessage) =>
            new AcquireResult(AcquireStatus.IoError, null, lockPath, errorMessage);
    }

    /// <summary>
    /// The held OS exclusion of a <see cref="JsonSaveCoordinator"/> path lock.
    /// Disposing releases the handle; the lock file itself is never replaced
    /// or deleted — its existence is not the exclusion, the handle is.
    /// </summary>
    internal sealed class JsonSaveLock : IDisposable
    {
        private FileStream? _stream;
        private SafeFileHandle? _handle;

        internal JsonSaveLock(string lockPath, FileStream stream)
        {
            LockPath = lockPath;
            _stream = stream;
        }

        internal JsonSaveLock(string lockPath, SafeFileHandle handle)
        {
            LockPath = lockPath;
            _handle = handle;
        }

        /// <summary>The lock file this lease holds.</summary>
        internal string LockPath { get; }

        public void Dispose()
        {
            var stream = _stream;
            _stream = null;
            var handle = _handle;
            _handle = null;
            stream?.Dispose();
            handle?.Dispose();
        }
    }

    /// <summary>What <see cref="JsonSaveCoordinator.ResolveOwnedPath"/> produced.</summary>
    internal enum PathResolveStatus
    {
        /// <summary>The path resolved inside the project root.</summary>
        Resolved = 0,

        /// <summary>The path is not usable at all (empty, malformed).</summary>
        Invalid = 1,

        /// <summary>The path resolves outside the project root — never locked or written.</summary>
        Refused = 2,

        /// <summary>The path's reparse points could not be resolved — fail closed.</summary>
        IoError = 3,
    }

    /// <summary>The outcome of <see cref="JsonSaveCoordinator.ResolveOwnedPath"/>.</summary>
    internal sealed class PathResolveResult
    {
        internal PathResolveResult(
            PathResolveStatus status, string? resolvedPath, string? errorMessage)
        {
            Status = status;
            ResolvedPath = resolvedPath;
            ErrorMessage = errorMessage;
        }

        internal PathResolveStatus Status { get; }

        /// <summary>The canonical absolute path on <see cref="PathResolveStatus.Resolved"/>.</summary>
        internal string? ResolvedPath { get; }

        /// <summary>A fixed English reason on failure.</summary>
        internal string? ErrorMessage { get; }
    }

    /// <summary>What a <see cref="JsonSaveCoordinator.PublishCreateAtomic"/> produced.</summary>
    internal enum PublishCreateStatus
    {
        /// <summary>The bytes were published at the target path.</summary>
        Created = 0,

        /// <summary>A destination existed before the create-only move landed — left untouched.</summary>
        AlreadyExists = 1,

        /// <summary>The durable temp write or the publish move failed — no partial target.</summary>
        IoError = 2,
    }

    /// <summary>The outcome of <see cref="JsonSaveCoordinator.PublishCreateAtomic"/>.</summary>
    internal sealed class PublishCreateResult
    {
        private PublishCreateResult(PublishCreateStatus status, string? errorMessage)
        {
            Status = status;
            ErrorMessage = errorMessage;
        }

        internal PublishCreateStatus Status { get; }

        /// <summary>A fixed English reason on <see cref="PublishCreateStatus.IoError"/>.</summary>
        internal string? ErrorMessage { get; }

        internal static PublishCreateResult Created() =>
            new PublishCreateResult(PublishCreateStatus.Created, null);

        internal static PublishCreateResult AlreadyExists() =>
            new PublishCreateResult(PublishCreateStatus.AlreadyExists, null);

        internal static PublishCreateResult IoError(string? errorMessage) =>
            new PublishCreateResult(PublishCreateStatus.IoError, errorMessage);
    }

    /// <summary>What <see cref="JsonSaveCoordinator.ReadIntent"/> found.</summary>
    internal enum IntentReadState
    {
        /// <summary>No intent record exists for the graph.</summary>
        Absent = 0,

        /// <summary>A well-formed record that names this graph and its path was found.</summary>
        Valid = 1,

        /// <summary>
        /// A record that does not parse, names another graph or another
        /// path, or carries a malformed hash — reported once, left
        /// untouched, never acted upon.
        /// </summary>
        Invalid = 2,
    }

    /// <summary>The outcome of <see cref="JsonSaveCoordinator.ReadIntent"/>.</summary>
    internal sealed class IntentReadResult
    {
        private IntentReadResult(
            IntentReadState state, JsonSyncIntent? intent, string? warning,
            string? recordHash)
        {
            State = state;
            Intent = intent;
            Warning = warning;
            RecordHash = recordHash;
        }

        internal IntentReadState State { get; }

        /// <summary>The validated record on <see cref="IntentReadState.Valid"/>.</summary>
        internal JsonSyncIntent? Intent { get; }

        /// <summary>The fixed English warning behind <see cref="IntentReadState.Invalid"/>.</summary>
        internal string? Warning { get; }

        /// <summary>
        /// Lowercase-hex SHA-256 of the record file's raw bytes as read —
        /// the snapshot <see cref="JsonSaveCoordinator.ClearIntentIfUnchanged"/>
        /// compares against; null when no record was read.
        /// </summary>
        internal string? RecordHash { get; }

        internal static readonly IntentReadResult Absent =
            new IntentReadResult(IntentReadState.Absent, null, null, null);

        internal static IntentReadResult Valid(JsonSyncIntent intent, string recordHash) =>
            new IntentReadResult(IntentReadState.Valid, intent, null, recordHash);

        internal static IntentReadResult Invalid(string warning, string? recordHash) =>
            new IntentReadResult(IntentReadState.Invalid, null, warning, recordHash);
    }

    /// <summary>
    /// A validated crash-recovery intent record: the write a process meant
    /// to finish when it died between publish and persist (SPEC §H7 item 3).
    /// </summary>
    internal sealed class JsonSyncIntent
    {
        internal JsonSyncIntent(
            string path,
            string graphGuid,
            string kind,
            string baseHash,
            string outputHash,
            string outputGuiHash)
        {
            Path = path;
            GraphGuid = graphGuid;
            Kind = kind;
            BaseHash = baseHash;
            OutputHash = outputHash;
            OutputGuiHash = outputGuiHash;
        }

        /// <summary>The OS-resolved absolute target path the write addressed.</summary>
        internal string Path { get; }

        /// <summary>The graph asset GUID the write belongs to.</summary>
        internal string GraphGuid { get; }

        /// <summary><c>create</c> or <c>update</c>.</summary>
        internal string Kind { get; }

        /// <summary>The disk revision the write was bound to — lowercase-hex SHA-256 or <c>absent</c>.</summary>
        internal string BaseHash { get; }

        /// <summary>Lowercase-hex SHA-256 of the published output bytes.</summary>
        internal string OutputHash { get; }

        /// <summary>The output's GUI-owned projection hash (canonical + layout).</summary>
        internal string OutputGuiHash { get; }
    }
}
