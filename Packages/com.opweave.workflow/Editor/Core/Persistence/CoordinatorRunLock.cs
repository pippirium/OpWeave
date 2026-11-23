#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The inter-process Run exclusion of SPEC §E2 item 3: an OS-level lock on
    /// <c>coordinator.lock</c> inside the injected Run Store root
    /// (<c>Library/OpWeave/</c> in Unity), plus the only read/write path to
    /// the persisted owner record <c>lock.json</c> (<see cref="RunLockState"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Lock method (SPEC §T.4 L-14).</b> The exclusion is an OS
    /// file handle on the normalized absolute path of
    /// <c>coordinator.lock</c>, held open for the whole critical section —
    /// a raw <c>CreateFileW</c> handle with share mode 0 (exclusive) that
    /// no <see cref="SafeHandle"/> owns — a real share-mode denial: every
    /// later open from any process, including this one, fails while the
    /// handle lives, and the OS releases it on abnormal process exit. v1
    /// supports the Windows Editor only (SPEC §E2 item 3 "v1 の対象外の
    /// OS", decision D): the cross-process exclusion is unproven on every
    /// other platform, so acquisition there is treated as unobtainable —
    /// the same answer a live owner's lock gives — before the file is
    /// opened or created; the macOS lock method is post-v1 (T-W4.6-07).
    /// A held handle is used instead of byte-range
    /// <see cref="FileStream.Lock"/> because POSIX <c>fcntl</c> locks drop
    /// the moment the process closes ANY descriptor on the same file, and
    /// instead of create/check lock-file tricks because neither file
    /// existence nor an atomic replace is an OS exclusion.
    /// <c>coordinator.lock</c> is therefore never replaced and never
    /// deleted, not even after release (SPEC §F3 item 2).</para>
    /// <para><b>Domain reload (SPEC §E2).</b> Managed objects die on a Unity
    /// domain reload but an OS file handle does not — and on Windows the
    /// held handle has no managed owner at all: no FileStream or SafeHandle
    /// exists whose finalizer or AppDomain-unload cleanup could invalidate
    /// it (Unity Mono invalidates managed handles on unload even when
    /// finalization is suppressed; a bare HANDLE survives because nothing
    /// managed references it).
    /// <see cref="LockHandleValue"/> exposes the raw handle for the Unity
    /// wiring to persist across the reload (SessionState lives exactly as
    /// long as the process, like the handle itself) and <see cref="TryAdopt"/>
    /// re-attaches managed ownership in the new domain — but only after the
    /// OS proves the value is this process's live exclusive holder of this
    /// root's coordinator.lock; anything else fails closed, and on platforms
    /// without that proof adoption always fails closed. A failed adoption is
    /// the SPEC §E2 "could not be kept" case: the caller must not resume.
    /// A matching <c>editorSessionId</c> alone is never proof of holding the
    /// lock (AGENTS invariant), and neither is a bare handle value.</para>
    /// <para><b>lock.json.</b> Every read, verification and update of
    /// lock.json goes through this object's held critical section
    /// (<see cref="ReadState"/>, <see cref="WriteState"/>, <see cref="Release"/>):
    /// updates are a same-directory temp file fsync'd and atomically
    /// replaced (SPEC §F3 item 2). <see cref="Release"/> empties lock.json
    /// (zero-length) before dropping the OS handle. <see cref="Dispose"/>
    /// drops only the handle — the recorded state deliberately survives for
    /// the SPEC §F4 recovery engine, the same residue an abnormal
    /// termination leaves.</para>
    /// <para><b>Failures.</b> Acquisition that cannot take the exclusion
    /// reports OW-RUN-002 — an unobtainable lock means the owner is alive
    /// and a live owner's lock is never taken (SPEC §E2); the RunCoordinator
    /// of T-W1.8-03 propagates it. lock.json persistence failures report
    /// OW-STORE-001, an unreadable recorded state reports OW-STORE-002.
    /// This is distinct from the development Coordinator claim lock of
    /// workspace/plans/DAG.md.</para>
    /// </remarks>
    public sealed class CoordinatorRunLock : IDisposable
    {
        /// <summary>The exclusion file name (SPEC §E2 item 3).</summary>
        public const string LockFileName = "coordinator.lock";

        /// <summary>The persisted owner-state file name (SPEC §E2 item 3).</summary>
        public const string StateFileName = "lock.json";

        private const string TempSuffix = ".opweave-tmp";
        private const int MaxReplaceAttempts = 5;
        private const int ReplaceRetryDelayMilliseconds = 15;

        private const string RunIdMember = "runId";
        private const string EditorSessionIdMember = "editorSessionId";
        private const string AcquiredAtMember = "acquiredAt";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// The fixed English cause the non-Windows guards of SPEC §E2 item 3
        /// (decision D) report: <see cref="TryAcquire"/> as OW-RUN-002 and
        /// <see cref="JsonSaveCoordinator"/>'s lock acquisition as its failed
        /// outcome.
        /// </summary>
        internal const string NonWindowsExclusionCause =
            "OpWeave v1 supports the Windows Editor only; the cross-process Run exclusion cannot be established on this platform.";

        /// <summary>
        /// The one platform read of SPEC §E2 item 3's "v1 の対象外の OS"
        /// (decision D): <see langword="true"/> while the Editor runs on the
        /// v1-supported Windows host. Production reads the same
        /// <see cref="PlatformID.Win32NT"/> test both lock acquisitions used;
        /// tests override the property to drive the fail-closed non-Windows
        /// paths and restore it afterwards. Never public.
        /// </summary>
        internal static bool IsWindowsPlatform { get; set; } =
            Environment.OSVersion.Platform == PlatformID.Win32NT;

        private readonly string _root;
        private FileStream? _stream;
        private IntPtr _rawHandle;
        private bool _held;
        private bool _disposed;

        private CoordinatorRunLock(string root, FileStream stream)
        {
            _root = root;
            _stream = stream;
            _held = true;
        }

        // The raw-handle form: the Windows acquisition and every adoption.
        // Ownership attaches to the raw OS handle itself — no
        // SafeFileHandle/FileStream wrapper exists whose runtime-specific
        // ownership, finalizer or domain-unload behavior could drop the
        // exclusion early or keep it open on close. Release/Dispose closes
        // the handle exactly once through the OS.
        private CoordinatorRunLock(string root, IntPtr rawHandle)
        {
            _root = root;
            _rawHandle = rawHandle;
            _held = true;
        }

        /// <summary>The normalized absolute Run Store root this lock covers.</summary>
        public string RootPath => _root;

        /// <summary>The normalized absolute path of <c>coordinator.lock</c>.</summary>
        public string LockFilePath => Path.Combine(_root, LockFileName);

        /// <summary>The normalized absolute path of <c>lock.json</c>.</summary>
        public string StateFilePath => Path.Combine(_root, StateFileName);

        /// <summary>True while this object owns the OS exclusion.</summary>
        public bool IsHeld => _held && !_disposed;

        /// <summary>
        /// The raw OS handle value of the held exclusion, or
        /// <see cref="IntPtr.Zero"/> when not held. The Unity wiring persists
        /// it across a domain reload (SPEC §E2) and re-adopts it with
        /// <see cref="TryAdopt"/>; the value is never a proof of holding on
        /// its own — adoption must verify it against the OS first.
        /// </summary>
        public IntPtr LockHandleValue
        {
            get
            {
                if (!IsHeld)
                {
                    return IntPtr.Zero;
                }
                return _stream is { } stream
                    ? stream.SafeFileHandle.DangerousGetHandle()
                    : _rawHandle;
            }
        }

        /// <summary>
        /// Non-blocking acquisition of the project-unique exclusion (SPEC
        /// §E2 item 3): opens <c>coordinator.lock</c> under
        /// <paramref name="rootPath"/> — normalized to an absolute path —
        /// creating it when missing but never replacing or deleting it. A
        /// failed acquisition means the owner is alive: the caller must not
        /// start, resume or modify the recorded Run. lock.json is not
        /// touched; state is read and written inside the held critical
        /// section by <see cref="ReadState"/>/<see cref="WriteState"/>.
        /// v1 supports the Windows Editor only (SPEC §E2 item 3, decision D):
        /// the cross-process exclusion is unproven on every other platform,
        /// so it is treated as unobtainable — OW-RUN-002 — before
        /// coordinator.lock is opened or created.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="rootPath"/> is null or empty.
        /// </exception>
        public static Result<CoordinatorRunLock> TryAcquire(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.", nameof(rootPath));
            }
            var root = Path.GetFullPath(rootPath);
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Result<CoordinatorRunLock>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_001,
                    "The Run Store root could not be created."));
            }
            // SPEC §E2 item 3 "v1 の対象外の OS" (decision D): off the
            // v1-supported Windows Editor the exclusion is unproven, so it is
            // unobtainable — the same answer a live owner's lock gives —
            // before coordinator.lock is opened or created.
            if (!IsWindowsPlatform)
            {
                return Result<CoordinatorRunLock>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_002, NonWindowsExclusionCause));
            }
            return TryAcquireWindows(root);
        }

        // The Windows acquisition: the exclusion is a raw CreateFileW
        // handle — share mode 0 denies every later open from any process,
        // OPEN_ALWAYS creates coordinator.lock when missing without ever
        // replacing it — that no SafeHandle owns, so nothing managed
        // (finalization, GC or AppDomain unload) can drop it silently.
        // It is released only by an explicit CloseHandle.
        private static Result<CoordinatorRunLock> TryAcquireWindows(string root)
        {
            IntPtr handle;
            try
            {
                handle = WindowsNative.OpenExclusiveFileHandle(
                    Path.Combine(root, LockFileName));
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Result<CoordinatorRunLock>.Failure(LockHeldDiagnostic());
            }
            if (handle == IntPtr.Zero || handle == WindowsNative.InvalidHandleValue)
            {
                return Result<CoordinatorRunLock>.Failure(LockHeldDiagnostic());
            }
            return Result<CoordinatorRunLock>.Success(new CoordinatorRunLock(root, handle));
        }

        private static Diagnostic LockHeldDiagnostic() =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_002,
                "The Run lock is held by a live owner; a live owner's lock is never taken.");

        /// <summary>
        /// Re-attaches managed ownership to a coordinator.lock OS handle
        /// that survived a domain reload (SPEC §E2): the value a previous
        /// domain's <see cref="LockHandleValue"/> returned, persisted by the
        /// caller (SessionState). lock.json is verified by the caller inside
        /// the adopted critical section; the file itself is never replaced
        /// or deleted.
        /// </summary>
        /// <remarks>
        /// <para>A raw handle value plus file existence is never proof of
        /// holding the exclusion (SPEC §E2 — a matching editorSessionId is
        /// never proof either): the value could be dead, closed and reused
        /// for an unrelated object, or a handle to another file. Adoption
        /// therefore succeeds only when the OS proves all of: (i) the value
        /// is a valid open file handle of this process, (ii) its final path
        /// is exactly this root's canonical absolute coordinator.lock path,
        /// and (iii) both a GENERIC_READ and a GENERIC_WRITE open of that
        /// path — each offering the full FILE_SHARE_READ|WRITE|DELETE
        /// mask — are denied with a sharing violation: only a share-mode-0
        /// holder denies both full-sharing probes, so an exclusive holder
        /// exists, and since the candidate refers to this file and
        /// coexists with that holder it can only be this handle.</para>
        /// <para>Every other outcome is the SPEC §E2 "could not be kept"
        /// case and fails closed — the caller must not resume its Run
        /// (NeedsReview path). That includes platforms with no
        /// handle-identity proof in this implementation: today only Windows
        /// can prove adoption, so every non-Windows platform fails closed
        /// (the macOS proof is T-W1.7-06's scope). On failure the candidate
        /// handle is never closed: releasing an unowned or unproven handle
        /// could silently drop a live exclusion.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// <paramref name="rootPath"/> is null or empty.
        /// </exception>
        public static Result<CoordinatorRunLock> TryAdopt(string rootPath, IntPtr lockHandleValue)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.", nameof(rootPath));
            }
            var root = Path.GetFullPath(rootPath);
            if (lockHandleValue == IntPtr.Zero || lockHandleValue == new IntPtr(-1))
            {
                return Result<CoordinatorRunLock>.Failure(StoreDiagnostic(
                    "The persisted Run lock handle is empty; the exclusion was lost."));
            }
            var lockPath = Path.Combine(root, LockFileName);
            if (!File.Exists(lockPath))
            {
                return Result<CoordinatorRunLock>.Failure(StoreDiagnostic(
                    "The Run lock file does not exist; the exclusion was lost."));
            }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return Result<CoordinatorRunLock>.Failure(StoreDiagnostic(
                    "The persisted Run lock handle cannot be verified on this platform; the exclusion could not be kept."));
            }
            return TryAdoptWindows(root, lockPath, lockHandleValue);
        }

        // The Windows adoption proof of TryAdopt's contract — reached only
        // after the zero/negative and missing-file rejections above.
        private static Result<CoordinatorRunLock> TryAdoptWindows(
            string root, string lockPath, IntPtr lockHandleValue)
        {
            // (i) + (ii): the value is a live file handle of this process
            // whose final path is this root's canonical coordinator.lock —
            // dead, reused, foreign and non-file values all resolve to
            // nothing or to a different path. Both sides come from
            // GetFinalPathNameByHandleW, so junction/symlink indirections
            // resolve identically (Path.GetFullPath cannot reproduce
            // resolved device paths).
            var finalPath = WindowsNative.GetFinalPathName(lockHandleValue);
            var expectedPath = ExpectedFinalLockPath(root);
            if (finalPath is null || expectedPath is null ||
                !string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                return Result<CoordinatorRunLock>.Failure(StoreDiagnostic(
                    "The persisted Run lock handle does not resolve to this Run Store's coordinator.lock; the exclusion could not be kept."));
            }
            // (iii): a sharing violation on one probe is not proof of an
            // exclusive holder — a same-path handle held read-only with
            // FILE_SHARE_READ already denies a read-write probe while
            // still permitting other readers. Only a share-mode-0 holder
            // denies BOTH a GENERIC_READ and a GENERIC_WRITE open that
            // each offer the full FILE_SHARE_READ|WRITE|DELETE mask (any
            // non-zero share mode permits at least one of them); the
            // adopted handle refers to this file (i)+(ii) and coexists
            // with that holder, so it must be that holder.
            if (!FullSharingProbeDenied(lockPath, WindowsNative.GenericReadAccess) ||
                !FullSharingProbeDenied(lockPath, WindowsNative.GenericWriteAccess))
            {
                return Result<CoordinatorRunLock>.Failure(StoreDiagnostic(
                    "The persisted Run lock handle is not the proven exclusive holder of coordinator.lock; the exclusion could not be kept."));
            }
            return Result<CoordinatorRunLock>.Success(new CoordinatorRunLock(root, lockHandleValue));
        }

        // The canonical final path this root's coordinator.lock resolves
        // to, produced by the same GetFinalPathNameByHandleW as the
        // candidate: the root directory is opened (a directory stays
        // openable while its children are locked) and its resolved device
        // path queried, so junction/symlink indirections on the requested
        // path normalize exactly like the candidate's. Null when the
        // directory cannot be probed — the caller then fails closed.
        private static string? ExpectedFinalLockPath(string root)
        {
            var directory = WindowsNative.OpenDirectoryHandle(root);
            if (directory == IntPtr.Zero || directory == new IntPtr(-1))
            {
                return null;
            }
            try
            {
                var final = WindowsNative.GetFinalPathName(directory);
                return final is null ? null : final + "\\" + LockFileName;
            }
            finally
            {
                WindowsNative.CloseHandle(directory);
            }
        }

        // One half of the (iii) exclusive-holder proof: a fresh CreateFileW
        // on the canonical lock path requesting desiredAccess while
        // offering the full FILE_SHARE_READ|WRITE|DELETE mask must be
        // denied with ERROR_SHARING_VIOLATION (32). A probe that opens
        // means some holder permits sharing — no exclusive holder exists —
        // so the probe handle is closed immediately and the proof fails;
        // any open failure other than a sharing violation proves nothing
        // either way and also fails.
        private static bool FullSharingProbeDenied(string lockPath, uint desiredAccess)
        {
            var probe = WindowsNative.OpenSharingProbe(lockPath, desiredAccess);
            if (probe == IntPtr.Zero || probe == WindowsNative.InvalidHandleValue)
            {
                return Marshal.GetLastWin32Error() ==
                    WindowsNative.SharingViolationWin32Error;
            }
            WindowsNative.CloseHandle(probe);
            return false;
        }

        // kernel32 entry points of the Windows acquisition, the adoption
        // proof and the exact-once raw-handle close. Reached only on
        // Windows — TryAcquire and TryAdopt check the platform first.
        private static class WindowsNative
        {
            internal const int SharingViolationWin32Error = 32;

            internal static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

            internal const uint GenericReadAccess = 0x80000000;
            internal const uint GenericWriteAccess = 0x40000000;

            private const uint FileShareNone = 0;
            private const uint FileShareReadWriteDelete = 0x00000007;
            private const uint OpenExisting = 3;
            private const uint OpenAlways = 4;
            private const uint FileAttributeNormal = 0x00000080;
            private const uint FileFlagBackupSemantics = 0x02000000;

            [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern uint GetFinalPathNameByHandleW(
                IntPtr hFile,
                [Out] char[] lpszFilePath,
                uint cchFilePath,
                uint dwFlags);

            [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr CreateFileW(
                string lpFileName,
                uint dwDesiredAccess,
                uint dwShareMode,
                IntPtr lpSecurityAttributes,
                uint dwCreationDisposition,
                uint dwFlagsAndAttributes,
                IntPtr hTemplateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr hObject);

            // The exclusion itself: share mode 0 denies every later open
            // from any process — including this one — while the handle
            // lives, and OPEN_ALWAYS creates coordinator.lock when missing
            // without ever replacing or truncating it (no DELETE_ON_CLOSE
            // flag: the file is never deleted). Returns
            // INVALID_HANDLE_VALUE when the exclusive open is denied.
            internal static IntPtr OpenExclusiveFileHandle(string path) =>
                CreateFileW(
                    path,
                    GenericReadAccess,
                    FileShareNone,
                    IntPtr.Zero,
                    OpenAlways,
                    FileAttributeNormal,
                    IntPtr.Zero);

            // A maximally sharing probe open for the adoption proof: the
            // caller asks for GenericReadAccess or GenericWriteAccess while
            // the probe offers the full FILE_SHARE_READ|WRITE|DELETE mask,
            // so only a share-mode-0 holder can deny it. Returns the raw
            // probe handle (the caller closes it immediately) or
            // INVALID_HANDLE_VALUE with the Win32 error in LastError.
            internal static IntPtr OpenSharingProbe(string path, uint desiredAccess) =>
                CreateFileW(
                    path,
                    desiredAccess,
                    FileShareReadWriteDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    FileAttributeNormal,
                    IntPtr.Zero);

            // A query-only handle on a directory (FILE_FLAG_BACKUP_SEMANTICS
            // is what makes a directory openable through CreateFileW), used
            // to resolve the requested lock root's canonical final path.
            internal static IntPtr OpenDirectoryHandle(string path) =>
                CreateFileW(
                    path,
                    GenericReadAccess,
                    FileShareReadWriteDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics,
                    IntPtr.Zero);

            // The handle's final path in VOLUME_NAME_DOS /
            // FILE_NAME_NORMALIZED form (dwFlags = 0), or null when the
            // value is not a live named-object handle of this process —
            // adoption proof step (i).
            internal static string? GetFinalPathName(IntPtr handle)
            {
                var capacity = 512;
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var buffer = new char[capacity];
                    var length = GetFinalPathNameByHandleW(
                        handle, buffer, (uint)buffer.Length, 0);
                    if (length == 0)
                    {
                        return null;
                    }
                    if (length < buffer.Length)
                    {
                        return new string(buffer, 0, (int)length);
                    }
                    capacity = checked(capacity * 4);
                }
                return null;
            }
        }

        /// <summary>
        /// Reads and verifies lock.json inside the held critical section
        /// (SPEC §E2 item 3). A missing or empty file is the released state
        /// and reads as null; a present document must carry
        /// <c>runId</c>, <c>editorSessionId</c> and <c>acquiredAt</c> as
        /// non-empty strings — anything else is not a readable lock state.
        /// </summary>
        /// <exception cref="InvalidOperationException">The lock is not held.</exception>
        /// <exception cref="ObjectDisposedException">The object is disposed.</exception>
        public Result<RunLockState?> ReadState()
        {
            RequireHeld();
            CriticalSectionProbe?.Invoke("read");
            var path = StateFilePath;
            try
            {
                if (!File.Exists(path))
                {
                    return Result<RunLockState?>.Success(null);
                }
                var text = File.ReadAllText(path, s_utf8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return Result<RunLockState?>.Success(null);
                }
                var parsed = CanonicalJson.Parse(text);
                if (!parsed.Ok || parsed.Value is not JObject document)
                {
                    return Result<RunLockState?>.Failure(CorruptDiagnostic());
                }
                var runId = RequiredStringMember(document, RunIdMember);
                var sessionId = RequiredStringMember(document, EditorSessionIdMember);
                var acquiredAt = RequiredStringMember(document, AcquiredAtMember);
                if (runId is null || sessionId is null || acquiredAt is null)
                {
                    return Result<RunLockState?>.Failure(CorruptDiagnostic());
                }
                return Result<RunLockState?>.Success(
                    new RunLockState(runId, sessionId, acquiredAt));
            }
            // Bytes that are not valid UTF-8 never become JSON text: the
            // strict decoder's DecoderFallbackException is the same corrupt
            // recorded state as unparseable JSON — OW-STORE-002, not an
            // escaping exception.
            catch (DecoderFallbackException)
            {
                return Result<RunLockState?>.Failure(CorruptDiagnostic());
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Result<RunLockState?>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_001,
                    "The lock state file could not be read."));
            }
        }

        /// <summary>
        /// Replaces lock.json inside the held critical section with exactly
        /// the SPEC §E2 members <c>runId</c>, <c>editorSessionId</c> and
        /// <c>acquiredAt</c> — the SPEC §F3 item 2 temp-file, fsync'd atomic
        /// replace, so a crash leaves the old or the new content, never a
        /// torn file.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// A member of <paramref name="state"/> is null or empty.
        /// </exception>
        /// <exception cref="InvalidOperationException">The lock is not held.</exception>
        /// <exception cref="ObjectDisposedException">The object is disposed.</exception>
        public Result<Unit> WriteState(RunLockState state)
        {
            if (state is null)
            {
                throw new ArgumentNullException(nameof(state));
            }
            if (string.IsNullOrEmpty(state.RunId) ||
                string.IsNullOrEmpty(state.EditorSessionId) ||
                string.IsNullOrEmpty(state.AcquiredAt))
            {
                throw new ArgumentException(
                    "The lock state requires runId, editorSessionId and acquiredAt.", nameof(state));
            }
            RequireHeld();
            var bytes = s_utf8.GetBytes(CanonicalJson.Serialize(new JObject
            {
                [RunIdMember] = state.RunId,
                [EditorSessionIdMember] = state.EditorSessionId,
                [AcquiredAtMember] = state.AcquiredAt,
            }));
            try
            {
                CriticalSectionProbe?.Invoke("write");
                ReplaceAtomically(StateFilePath, bytes);
                return Result<Unit>.Success(Unit.Value);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_001,
                    "The lock state file could not be written."));
            }
        }

        /// <summary>
        /// The orderly Run end of SPEC §E2 item 3: empties lock.json to a
        /// zero-length file inside the still-held critical section, then
        /// drops the OS exclusion. The emptying happens strictly before the
        /// release; if the atomic replace itself fails the write failure is
        /// reported and the OS exclusion stays held — the caller can retry
        /// Release — because only a successful emptying may drop the
        /// handle.
        /// </summary>
        /// <exception cref="InvalidOperationException">The lock is not held.</exception>
        /// <exception cref="ObjectDisposedException">The object is disposed.</exception>
        public Result<Unit> Release()
        {
            RequireHeld();
            try
            {
                ReplaceAtomically(StateFilePath, Array.Empty<byte>());
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_001,
                    "The lock state file could not be emptied before release."));
            }
            CriticalSectionProbe?.Invoke("release");
            CloseHandle();
            _held = false;
            return Result<Unit>.Success(Unit.Value);
        }

        /// <summary>
        /// Drops only the OS exclusion — the abandoned-state semantics of an
        /// abnormal end (SPEC §E2): lock.json is intentionally not emptied,
        /// so the recorded owner state remains for the SPEC §F4 recovery
        /// engine. Use <see cref="Release"/> for the orderly Run end.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            CloseHandle();
            _held = false;
        }

        // Internal test seam (same pattern as FileRunStore's probes): invoked
        // inside the held critical section before every lock.json mutation
        // point — "read", "write" and "release" (the last between the
        // emptying write and the OS-handle close) — so tests assert every
        // lock.json access happens under the OS exclusion. Production callers
        // leave it null.
        internal Action<string>? CriticalSectionProbe { get; set; }

        private void RequireHeld()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CoordinatorRunLock));
            }
            if (!_held)
            {
                throw new InvalidOperationException("The Run lock is not held.");
            }
        }

        private void CloseHandle()
        {
            var stream = _stream;
            _stream = null;
            stream?.Dispose();
            var raw = _rawHandle;
            _rawHandle = IntPtr.Zero;
            if (raw != IntPtr.Zero)
            {
                // Exact-once close on every runtime: the raw value is
                // released through the OS directly — no SafeFileHandle or
                // FileStream ownership semantics are involved.
                WindowsNative.CloseHandle(raw);
            }
        }

        // SPEC §F3 item 2: a temp file in the same directory is fsync'd, then
        // atomically moved over lock.json, so a crash leaves the old or the
        // new content — never a torn file. The replace step retries the
        // bounded transient Win32 sharing failures an external reader
        // (indexer, virus scanner) causes while it briefly holds a file —
        // the same failures FileRunStore handles.
        private static void ReplaceAtomically(string path, byte[] bytes)
        {
            var temp = path + TempSuffix;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
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
                catch (Exception e)
                {
                    if (!IsTransientReplaceFailure(e) || attempt >= MaxReplaceAttempts)
                    {
                        TryDeleteTemp(temp);
                        throw;
                    }
                    Thread.Sleep(ReplaceRetryDelayMilliseconds);
                }
            }
        }

        // The transient Win32 codes the replace step surfaces while an
        // external reader holds a file: ERROR_ACCESS_DENIED (5),
        // ERROR_SHARING_VIOLATION (32) and ERROR_UNABLE_TO_REMOVE_REPLACED
        // (1175).
        private static bool IsTransientReplaceFailure(Exception e)
        {
            if (!(e is IOException || e is UnauthorizedAccessException))
            {
                return false;
            }
            var code = e.HResult & 0xFFFF;
            return code == 5 || code == 32 || code == 1175;
        }

        private static void TryDeleteTemp(string temp)
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception e) when (
                e is IOException || e is UnauthorizedAccessException ||
                e is System.Security.SecurityException || e is NotSupportedException)
            {
            }
        }

        // A required member is readable only as a non-empty JSON string;
        // an absent member or any other token type — object, array,
        // number, boolean, null — reads as no recorded lock state, never
        // as a conversion exception.
        private static string? RequiredStringMember(JObject document, string name) =>
            document[name] is JValue { Type: JTokenType.String, Value: string text } &&
            text.Length != 0
                ? text
                : null;

        private static bool IsIoFailure(Exception e) =>
            e is IOException || e is UnauthorizedAccessException ||
            e is NotSupportedException || e is System.Security.SecurityException;

        private static Diagnostic StoreDiagnostic(string message) =>
            ErrorCodes.Create(ErrorCodes.OW_STORE_001, message);

        private static Diagnostic CorruptDiagnostic() =>
            ErrorCodes.Create(
                ErrorCodes.OW_STORE_002,
                "The lock state file is not a readable lock state document.");
    }
}
