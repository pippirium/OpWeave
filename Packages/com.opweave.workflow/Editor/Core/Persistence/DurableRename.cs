#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The durable rename behind the SPEC §0.6 SG-16 tombstone publish:
    /// the step that moves a same-directory, already fsync'd temp file
    /// over its committed path so the <em>directory entry</em> — not
    /// only the file contents — survives a crash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="File.Move(string, string)"/> /
    /// <see cref="File.Replace(string, string, string)"/> guarantee the
    /// atomicity of the rename but not its durability: flushing the
    /// file's bytes never flushes the directory entry that names them,
    /// so a crash just after the move can lose the committed name
    /// entirely — the gap SG-16 closes for the deletion tombstone, whose
    /// record must be durable on disk before the Run directory and
    /// index entry it replaces may be removed.
    /// </para>
    /// <para>
    /// Windows: <c>MoveFileExW</c> with
    /// <c>MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH</c> — the
    /// documented durable rename; the write-through flag makes the call
    /// return only once the move has been flushed to the storage
    /// device. macOS / Linux (the Editor's other platforms):
    /// <c>rename(2)</c> publishes the entry atomically, then an
    /// <c>fsync</c> of the containing directory makes the entry itself
    /// durable — a rename without the directory fsync commits the entry
    /// only to the page cache.
    /// </para>
    /// <para>
    /// Every native return value is checked and every failure surfaces
    /// as <see cref="IOException"/> carrying the native error code in
    /// <see cref="Exception.HResult"/> — the same shape
    /// <see cref="File.Move(string, string)"/> produced — so the
    /// caller's transient sharing-failure classification keeps working.
    /// </para>
    /// </remarks>
    internal static class DurableRename
    {
        private const uint MoveFileReplaceExisting = 0x00000001;
        private const uint MoveFileWriteThrough = 0x00000008;
        private const int ReadOnly = 0;

        /// <summary>
        /// Moves the fsync'd <paramref name="tempPath"/> over
        /// <paramref name="targetPath"/> — creating or atomically
        /// replacing the target — and returns only once the rename
        /// itself is durable on disk. Throws <see cref="IOException"/>
        /// when any step fails; the temp file is still the caller's to
        /// keep or clean up.
        /// </summary>
        internal static void Move(string tempPath, string targetPath)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                if (!Native.MoveFileExW(
                        tempPath, targetPath,
                        MoveFileReplaceExisting | MoveFileWriteThrough))
                {
                    throw LastError("The durable rename could not publish the file");
                }
                return;
            }
            MovePosix(tempPath, targetPath);
        }

        // rename(2) publishes the directory entry atomically; the fsync
        // of the containing directory then makes that entry durable —
        // every return value checked, so a failure anywhere means the
        // publish is not proven and the caller must not proceed to the
        // destructive half.
        private static void MovePosix(string tempPath, string targetPath)
        {
            if (Native.rename(tempPath, targetPath) != 0)
            {
                throw LastError("The durable rename could not publish the file");
            }
            var directory =
                Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? ".";
            var descriptor = Native.open(directory, ReadOnly, 0);
            if (descriptor < 0)
            {
                throw LastError("The containing directory could not be opened");
            }
            try
            {
                if (Native.fsync(descriptor) != 0)
                {
                    throw LastError("The containing directory could not be fsync'd");
                }
            }
            finally
            {
                if (Native.close(descriptor) != 0)
                {
                    throw LastError("The directory handle could not be closed");
                }
            }
        }

        // The failure surface File.Move produced: an IOException whose
        // HResult is FACILITY_WIN32 | code, carrying the Win32 error or
        // the errno the failed call left behind, so the caller's
        // transient-failure classification keeps working.
        private static IOException LastError(string message)
        {
            var error = Marshal.GetLastWin32Error();
            return new IOException(
                message + " (error " +
                    error.ToString(CultureInfo.InvariantCulture) + ").",
                unchecked((int)0x80070000) | (error & 0xFFFF));
        }

        private static class Native
        {
            [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool MoveFileExW(
                string lpExistingFileName, string lpNewFileName, uint dwFlags);

            [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
            internal static extern int rename(string oldpath, string newpath);

            [DllImport("libc", EntryPoint = "open", SetLastError = true)]
            internal static extern int open(string pathname, int flags, int mode);

            [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
            internal static extern int fsync(int fd);

            [DllImport("libc", EntryPoint = "close", SetLastError = true)]
            internal static extern int close(int fd);
        }
    }
}
