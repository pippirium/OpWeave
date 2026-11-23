#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Model;

namespace OpWeave.GraphToolkit
{
    /// <summary>The outcome of an SG-17 guarded <see cref="GraphJsonFile.SaveToFile"/>.</summary>
    internal enum GraphJsonSaveStatus
    {
        /// <summary>The merged JSON was written to the file.</summary>
        Saved = 0,

        /// <summary>
        /// The on-disk document's CanonicalWorkflow hash differs from the
        /// baseline captured when the graph was last synced — or the file was
        /// deleted — outside the GUI, and automatic overwrite is refused
        /// until a person explicitly reloads the JSON or overwrites it (SPEC
        /// §0.6 SG-17). The file is left untouched (or uncreated).
        /// </summary>
        ExternalModification = 1,

        /// <summary>
        /// The on-disk text does not parse as a workflow document; overwriting
        /// it is refused even for an explicit human overwrite — unparseable
        /// content is never destroyed (SPEC §0.6 SG-17). The file is left
        /// untouched.
        /// </summary>
        InvalidJson = 2,

        /// <summary>
        /// The graph's exported content cannot form a valid document
        /// (<see cref="WorkflowDocument.Create"/> failed), or the export
        /// refused to re-emit carried content whose references no longer
        /// resolve uniquely and exactly — silently losing it would guess
        /// (SPEC §0.6 SG-17). The file is left untouched; only an explicit
        /// human overwrite drops the affected items and proceeds.
        /// </summary>
        InvalidGraph = 3,

        /// <summary>The file could not be read or written; <see cref="GraphJsonSaveResult.ErrorMessage"/> carries the exception message.</summary>
        IoError = 4,
    }

    /// <summary>The result of <see cref="GraphJsonFile.SaveToFile"/>.</summary>
    /// <remarks>
    /// <see cref="Document"/> is the merged document that was written on
    /// <see cref="GraphJsonSaveStatus.Saved"/> (the new baseline the GUI
    /// records), and the conflicting on-disk document on
    /// <see cref="GraphJsonSaveStatus.ExternalModification"/>.
    /// <see cref="Diagnostics"/> carries the §A3 read diagnostics of the
    /// on-disk document whenever it was read, most importantly the failures
    /// behind <see cref="GraphJsonSaveStatus.InvalidJson"/> and
    /// <see cref="GraphJsonSaveStatus.InvalidGraph"/>.
    /// </remarks>
    internal sealed class GraphJsonSaveResult
    {
        internal GraphJsonSaveResult(
            GraphJsonSaveStatus status,
            WorkflowDocument? document,
            IReadOnlyList<Diagnostic>? diagnostics,
            string? errorMessage)
        {
            Status = status;
            Document = document;
            Diagnostics = diagnostics ?? Array.Empty<Diagnostic>();
            ErrorMessage = errorMessage;
        }

        internal GraphJsonSaveStatus Status { get; }

        internal WorkflowDocument? Document { get; }

        internal IReadOnlyList<Diagnostic> Diagnostics { get; }

        internal string? ErrorMessage { get; }
    }

    /// <summary>The outcome of a <see cref="GraphJsonFile.PublishCreate"/>/<see cref="GraphJsonFile.PublishReplace"/> publish attempt.</summary>
    internal enum GraphJsonPublishStatus
    {
        /// <summary>The output bytes were published at the destination.</summary>
        Published = 0,

        /// <summary>
        /// A create-only publish was refused: a file already occupies the
        /// destination — including one planted after the absence check and
        /// before the publish move — and is never overwritten (SPEC §H7
        /// item 3).
        /// </summary>
        AlreadyExists = 1,

        /// <summary>
        /// An expected-hash replace was refused: the destination's bytes no
        /// longer match the revision the caller read — or the file
        /// vanished — so writing would silently overwrite an external
        /// change (SPEC §H7 item 3).
        /// </summary>
        ChangedSinceRead = 2,

        /// <summary>The file could not be read or written; <see cref="GraphJsonPublishResult.ErrorMessage"/> carries the exception message.</summary>
        IoError = 3,
    }

    /// <summary>The result of a publish primitive.</summary>
    internal sealed class GraphJsonPublishResult
    {
        internal GraphJsonPublishResult(GraphJsonPublishStatus status, string? errorMessage)
        {
            Status = status;
            ErrorMessage = errorMessage;
        }

        internal GraphJsonPublishStatus Status { get; }

        internal string? ErrorMessage { get; }

        internal bool Published => Status == GraphJsonPublishStatus.Published;
    }

    /// <summary>
    /// Writes the graph's JSON form to disk under the SPEC §0.6 SG-17 rules:
    /// the GUI never silently overwrites the JSON source of truth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Immediately before writing, the current file is re-read and its
    /// CanonicalWorkflow hash is compared with the baseline the caller holds
    /// from the last sync. A hash that differs means an out-of-band change
    /// (an Agent edit, a manual edit): an automatic save — an Agent write or
    /// autosave — is refused with
    /// <see cref="GraphJsonSaveStatus.ExternalModification"/> and stays
    /// refused on every retry until a person explicitly reloads the JSON
    /// (re-import, which moves the baseline) or chooses "Overwrite JSON"
    /// (<paramref name="explicitOverwrite"/>). A file that does not parse is
    /// never overwritten — <see cref="GraphJsonSaveStatus.InvalidJson"/>
    /// applies to automatic and explicit writes alike.
    /// </para>
    /// <para>
    /// The written document merges the graph's seven owned keys with the
    /// <em>current file's</em> non-owned members (the baseline's when the file
    /// does not exist), so externally updated <c>title</c>,
    /// <c>description</c>, <c>tags</c> and <c>x-*</c> metadata survives a save
    /// — a metadata-only external edit changes no canonical content, keeps
    /// the hash equal and is therefore preserved rather than reported.
    /// </para>
    /// </remarks>
    internal static class GraphJsonFile
    {
        // UTF-8 without BOM; the §A3 writer emits LF and a trailing newline.
        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        // The shared bounded retry both replace paths run on a transient
        // hold of the destination (SPEC §H7 item 3): the first delay is
        // ReplaceRetryFirstDelayMilliseconds and doubles on each later
        // failure, capped at ReplaceRetryMaxDelayMilliseconds per delay; a
        // further attempt runs only while the time since the first failed
        // attempt plus the next delay stays within
        // ReplaceRetryBudgetMilliseconds, then the call fails closed.
        private const int ReplaceRetryFirstDelayMilliseconds = 50;
        private const int ReplaceRetryMaxDelayMilliseconds = 500;
        private const int ReplaceRetryBudgetMilliseconds = 2000;

        private static readonly Stopwatch s_retryStopwatch = Stopwatch.StartNew();
        private static Func<long> s_retryClock = () => s_retryStopwatch.ElapsedMilliseconds;
        private static Action<int> s_retrySleep = Thread.Sleep;

        /// <summary>
        /// The clock the shared replace-retry budget measures elapsed time
        /// against. Production keeps the monotonic stopwatch; tests
        /// substitute a fake clock so the budget is exercised quickly and
        /// deterministically, and restore it afterwards.
        /// </summary>
        /// <exception cref="ArgumentNullException">The assigned provider is null.</exception>
        internal static Func<long> ReplaceRetryClock
        {
            get => s_retryClock;
            set => s_retryClock = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// The delay primitive the replace-retry budget waits through.
        /// Production sleeps for real; tests substitute a fake delay —
        /// typically one that advances their fake
        /// <see cref="ReplaceRetryClock"/> — and restore it afterwards.
        /// </summary>
        /// <exception cref="ArgumentNullException">The assigned provider is null.</exception>
        internal static Action<int> ReplaceRetrySleep
        {
            get => s_retrySleep;
            set => s_retrySleep = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Saves <paramref name="graph"/> as <c>.opweave.json</c> at
        /// <paramref name="path"/> under the SG-17 overwrite rules (see the
        /// class remarks). <paramref name="baseline"/> is the document the
        /// graph was last synced with; its CanonicalWorkflow hash is the
        /// reference the on-disk file is checked against.
        /// </summary>
        /// <paramref name="beforeReplace"/> is a test seam invoked with the
        /// destination path on every replace attempt, after the
        /// destination's bytes were re-verified against the bytes this call
        /// read and immediately before the replace.
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal static GraphJsonSaveResult SaveToFile(
            OpWeaveGraph graph,
            string path,
            WorkflowDocument baseline,
            bool explicitOverwrite,
            Action<string>? beforeReplace = null)
        {
            if (graph is null)
            {
                throw new ArgumentNullException(nameof(graph));
            }
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            if (baseline is null)
            {
                throw new ArgumentNullException(nameof(baseline));
            }

            var disk = Read(path);
            if (disk.ErrorMessage is not null)
            {
                return new GraphJsonSaveResult(
                    GraphJsonSaveStatus.IoError, null, null, disk.ErrorMessage);
            }

            var nonOwnedSource = baseline;
            var diskText = disk.Text;
            if (disk.Exists)
            {
                var read = disk.Document!.Value;
                if (!read.Ok)
                {
                    return new GraphJsonSaveResult(
                        GraphJsonSaveStatus.InvalidJson, null, read.Diagnostics, null);
                }
                var diskDocument = read.Value!;
                var externallyModified = !string.Equals(
                    diskDocument.Canonical.WorkflowHash,
                    baseline.Canonical.WorkflowHash,
                    StringComparison.Ordinal);
                if (externallyModified && !explicitOverwrite)
                {
                    return new GraphJsonSaveResult(
                        GraphJsonSaveStatus.ExternalModification,
                        diskDocument,
                        read.Diagnostics,
                        null);
                }
                nonOwnedSource = diskDocument;
            }
            else if (!explicitOverwrite)
            {
                // The baseline implies a synced file existed; its absence is
                // an out-of-band delete — an external change to report, not a
                // file to silently recreate (SPEC §0.6 SG-17). An explicit
                // human overwrite proceeds with the baseline's metadata. The
                // same branch covers a graph that was never synced to this
                // path at all: the first save of a brand-new graph therefore
                // needs explicitOverwrite: true.
                return new GraphJsonSaveResult(
                    GraphJsonSaveStatus.ExternalModification, null, null, null);
            }

            var merged = GraphJsonConverter.Export(graph, nonOwnedSource);
            if (!merged.Ok)
            {
                return new GraphJsonSaveResult(
                    GraphJsonSaveStatus.InvalidGraph, null, merged.Diagnostics, null);
            }
            // A successful export's diagnostics are the carried items it
            // refused to re-emit because their references no longer resolve
            // uniquely and exactly (SPEC §0.6 SG-17): writing would drop
            // them silently, so the save is refused unless the human
            // explicitly chooses "Overwrite JSON" — which drops the
            // affected items by choice.
            if (merged.Diagnostics.Count != 0 && !explicitOverwrite)
            {
                return new GraphJsonSaveResult(
                    GraphJsonSaveStatus.InvalidGraph, null, merged.Diagnostics, null);
            }

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }
                var bytes = BuildOutput(merged.Value!, diskText).Bytes;
                if (disk.Exists)
                {
                    // The replace path re-verifies the destination against
                    // the bytes this call read before every attempt: a
                    // changed or vanished file is an external change to
                    // refuse on — consent to overwrite binds to the read
                    // revision (SPEC §H7 item 3) — never to overwrite.
                    var refused = WriteAtomicReplace(
                        path, bytes, disk.BytesHash!, beforeReplace);
                    if (refused is not null)
                    {
                        return refused;
                    }
                }
                else
                {
                    WriteAtomicCreate(path, bytes);
                }
            }
            catch (Exception e) when (IsIoError(e))
            {
                return new GraphJsonSaveResult(GraphJsonSaveStatus.IoError, null, null, e.Message);
            }
            return new GraphJsonSaveResult(
                GraphJsonSaveStatus.Saved, merged.Value, merged.Diagnostics, null);
        }

        // ── Revision and publication primitives (SPEC §H7 item 3) ──────────

        /// <summary>
        /// A read of the file at one path: the raw text, the lowercase-hex
        /// SHA-256 of its raw bytes, and the §A3 document parse.
        /// <see cref="BytesHash"/> is the byte-level revision a later
        /// <see cref="PublishReplace"/> compares against; <see cref="Text"/>
        /// supplies the raw member values <see cref="BuildOutput"/> carries
        /// byte-exactly.
        /// </summary>
        internal sealed class DiskRead
        {
            internal DiskRead(
                bool exists,
                string? text,
                string? bytesHash,
                Result<WorkflowDocument>? document,
                string? errorMessage)
            {
                Exists = exists;
                Text = text;
                BytesHash = bytesHash;
                Document = document;
                ErrorMessage = errorMessage;
            }

            /// <summary>A file exists at the read path.</summary>
            internal bool Exists { get; }

            /// <summary>The file's raw text; null when absent or unreadable.</summary>
            internal string? Text { get; }

            /// <summary>Lowercase-hex SHA-256 of the file's raw bytes; null when absent or unreadable.</summary>
            internal string? BytesHash { get; }

            /// <summary>The §A3 parse of <see cref="Text"/>; null when absent or unreadable.</summary>
            internal Result<WorkflowDocument>? Document { get; }

            /// <summary>The exception message on an I/O failure; null otherwise.</summary>
            internal string? ErrorMessage { get; }
        }

        /// <summary>
        /// The output ready to publish: the merged document written by the
        /// §A3 writer with the non-owned members carried byte-exactly from
        /// the text they were last read from.
        /// </summary>
        internal sealed class Output
        {
            internal Output(string text, byte[] bytes)
            {
                Text = text;
                Bytes = bytes;
            }

            /// <summary>
            /// The document text (trailing newline, no BOM). Owned members and
            /// member separators keep the §A3 writer's LF formatting; non-owned
            /// values carried byte-exactly keep the bytes they were read with,
            /// so a carried multi-line value may embed its original line
            /// endings.
            /// </summary>
            internal string Text { get; }

            /// <summary><see cref="Text"/> as UTF-8 without BOM — the bytes the publish primitives write.</summary>
            internal byte[] Bytes { get; }
        }

        /// <summary>
        /// Reads the file at <paramref name="path"/> once: its raw bytes are
        /// hashed for <see cref="PublishReplace"/>'s expected revision and
        /// decoded for the §A3 parse the result carries. A file that vanishes
        /// between the existence check and the byte read reports
        /// <see cref="DiskRead.Exists"/> true with
        /// <see cref="DiskRead.ErrorMessage"/> set (and null
        /// <see cref="DiskRead.Text"/>/<see cref="DiskRead.BytesHash"/>/
        /// <see cref="DiskRead.Document"/>) — callers treat that as an I/O
        /// error, not an absent file.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        internal static DiskRead Read(string path)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            try
            {
                if (!File.Exists(path))
                {
                    return new DiskRead(false, null, null, null, null);
                }
                var bytes = File.ReadAllBytes(path);
                // File.ReadAllText semantics: a UTF-8 BOM is a byte prefix,
                // not text — BytesHash keeps hashing the raw bytes.
                var text = DecodeUtf8(bytes);
                return new DiskRead(
                    true, text, Sha256Hex(bytes), WorkflowSerializer.Read(text), null);
            }
            catch (Exception e) when (IsIoError(e))
            {
                return new DiskRead(true, null, null, null, e.Message);
            }
        }

        /// <summary>
        /// The revision of the document's GUI-owned content (SPEC §H7 item
        /// 3): the SHA-256 of the Canonical JSON of
        /// <c>{"canonical":&lt;CanonicalWorkflow&gt;,"layout":&lt;§A8 layout&gt;}</c>
        /// — equal when only the non-owned metadata (<c>title</c>,
        /// <c>description</c>, <c>tags</c>, <c>x-*</c>) differs, different
        /// when <c>layout</c>, <c>id</c>, <c>schemaVersion</c> or any other
        /// CanonicalWorkflow content differs. The §A4 canonical object
        /// already covers <c>id</c>/<c>schemaVersion</c> and drops exactly
        /// the non-owned metadata (SPEC §A4, §D0.2); <c>layout</c> is the
        /// seventh GUI-owned key it does not carry.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
        internal static string GuiOwnedHash(WorkflowDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }
            var input = new JObject { ["canonical"] = document.Canonical.ToToken() };
            var layout = LayoutToken(document);
            if (layout is not null)
            {
                input["layout"] = layout;
            }
            return CanonicalJson.Sha256(input);
        }

        /// <summary>
        /// The §A8 <c>layout</c> member of <paramref name="document"/> as a
        /// token — <c>{"nodes":{"&lt;id&gt;":{"x","y"}},"notes":[…]}</c> in model
        /// order — or null when the document carries no layout.
        /// </summary>
        internal static JObject? LayoutToken(WorkflowDocument? document)
        {
            var layout = document?.Layout;
            if (layout is null)
            {
                return null;
            }
            var nodes = new JObject();
            foreach (var pair in layout.Nodes)
            {
                nodes[pair.Key] = new JObject
                {
                    ["x"] = pair.Value.X,
                    ["y"] = pair.Value.Y,
                };
            }
            var notes = new JArray();
            foreach (var note in layout.Notes)
            {
                notes.Add(new JObject
                {
                    ["id"] = note.Id,
                    ["x"] = note.X,
                    ["y"] = note.Y,
                    ["text"] = note.Text,
                });
            }
            return new JObject { ["nodes"] = nodes, ["notes"] = notes };
        }

        /// <summary>
        /// Writes <paramref name="merged"/> with the §A3 writer, then carries
        /// the non-owned members — <c>title</c>, <c>description</c>,
        /// <c>tags</c> and every <c>x-*</c> member — in with the raw value
        /// text they had in <paramref name="carryText"/>, the text they were
        /// last read from: an external metadata edit survives a save
        /// byte-exactly, escapes and all (SPEC §0.6 SG-17, §H7 item 3).
        /// Raw text is carried only while the carried member's value parses
        /// equal to the merged member's value — the carry preserves bytes, it
        /// never reintroduces stale content, so a carried member whose value
        /// differs keeps the writer's merged text. A metadata member the
        /// writer omitted — an explicit <c>"tags": []</c> or <c>null</c>, a
        /// null <c>title</c> or <c>description</c> the model cannot tell from
        /// absent — is re-inserted at its §A2 slot; any other member the
        /// merged document does not carry is never materialized.
        /// </summary>
        /// <remarks>
        /// <paramref name="carryText"/> must be text that parsed Ok under the
        /// §A3 reader (<see cref="WorkflowSerializer.Read"/>): the member
        /// scanner assumes valid strict JSON grammar. Any other text is
        /// refused with <see cref="ArgumentException"/> — which
        /// <see cref="SaveToFile"/>'s I/O boundary reports as
        /// <see cref="GraphJsonSaveStatus.IoError"/> — never an uncaught
        /// parse failure from inside the scanner.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="merged"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="carryText"/> is not text that parsed Ok under the
        /// §A3 reader.
        /// </exception>
        internal static Output BuildOutput(WorkflowDocument merged, string? carryText)
        {
            if (merged is null)
            {
                throw new ArgumentNullException(nameof(merged));
            }
            var written = WorkflowSerializer.Write(merged);
            if (carryText is not null)
            {
                // The member scanner assumes valid strict JSON; a carry
                // text that never parsed Ok is refused outright so a
                // malformed source fails as a result through the caller's
                // I/O boundary (ArgumentException is an IsIoError member),
                // never an uncaught FormatException from inside the
                // scanner.
                if (!WorkflowSerializer.Read(carryText).Ok)
                {
                    throw new ArgumentException(
                        "carryText must be text that parsed Ok under the §A3 reader.",
                        nameof(carryText));
                }
                written = CarryMetadataMembers(written, carryText);
                written = CarryExtensionMembers(written, carryText);
            }
            return new Output(written, s_utf8.GetBytes(written));
        }

        /// <summary>
        /// Publishes <paramref name="output"/> at <paramref name="path"/> as
        /// a create-only write (SPEC §H7 item 3): a destination that already
        /// exists is refused with <see cref="GraphJsonPublishStatus.AlreadyExists"/>,
        /// including a file planted between the absence check and the
        /// publish move — <see cref="File.Move(string, string)"/> performs
        /// no overwrite, so the planted file is never replaced.
        /// <paramref name="beforeMove"/> is a test seam invoked with the
        /// destination path after the absence check and the temp write,
        /// immediately before the publish move.
        /// </summary>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        internal static GraphJsonPublishResult PublishCreate(
            string path, Output output, Action<string>? beforeMove = null)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            if (output is null)
            {
                throw new ArgumentNullException(nameof(output));
            }
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }
                if (File.Exists(path))
                {
                    return new GraphJsonPublishResult(
                        GraphJsonPublishStatus.AlreadyExists, null);
                }
                var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    WriteTemp(tempPath, output.Bytes);
                    beforeMove?.Invoke(path);
                    File.Move(tempPath, path);
                }
                finally
                {
                    DeleteTemp(tempPath);
                }
                return new GraphJsonPublishResult(GraphJsonPublishStatus.Published, null);
            }
            catch (Exception e) when (IsIoError(e))
            {
                // A destination that exists after a failed move means the
                // create was refused — the planted file was never touched.
                return new GraphJsonPublishResult(
                    File.Exists(path)
                        ? GraphJsonPublishStatus.AlreadyExists
                        : GraphJsonPublishStatus.IoError,
                    e.Message);
            }
        }

        /// <summary>
        /// Publishes <paramref name="output"/> over <paramref name="path"/>
        /// only while the destination still carries the byte revision the
        /// caller read (SPEC §H7 item 3). The output is written to a
        /// same-directory temp file and flushed first; then on every replace
        /// attempt the destination is re-read and its bytes re-hashed
        /// immediately before the atomic replace, and any difference from
        /// <paramref name="expectedBytesHash"/> — a changed file or a
        /// vanished one — refuses the write as
        /// <see cref="GraphJsonPublishStatus.ChangedSinceRead"/>. A transient
        /// hold on the destination (a sharing or lock violation, or access
        /// denied — <see cref="IsTransientReplaceFailure"/>) usually means
        /// another process is writing the file, so the retry re-verifies
        /// before replacing: a hold that releases into new content is
        /// refused rather than overwriting the external bytes, while one on
        /// an unchanged destination is retried within the shared bounded
        /// budget before the publish fails closed. The final
        /// comparison is not a compare-and-swap; the recorded guarantee
        /// scope applies.
        /// <paramref name="beforeReplace"/> is a test seam invoked with the
        /// destination path on every attempt, after the destination's bytes
        /// were re-verified and immediately before the replace.
        /// </summary>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        internal static GraphJsonPublishResult PublishReplace(
            string path, Output output, string expectedBytesHash,
            Action<string>? beforeReplace = null)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            if (output is null)
            {
                throw new ArgumentNullException(nameof(output));
            }
            if (expectedBytesHash is null)
            {
                throw new ArgumentNullException(nameof(expectedBytesHash));
            }
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                try
                {
                    WriteTemp(tempPath, output.Bytes);
                    var retry = new ReplaceRetry();
                    for (;;)
                    {
                        byte[] current;
                        try
                        {
                            current = File.ReadAllBytes(path);
                        }
                        catch (Exception e) when (IsIoError(e))
                        {
                            // A file that vanished since the read is a
                            // change to refuse on; every other read failure
                            // is an I/O error.
                            return new GraphJsonPublishResult(
                                e is FileNotFoundException ||
                                e is DirectoryNotFoundException
                                    ? GraphJsonPublishStatus.ChangedSinceRead
                                    : GraphJsonPublishStatus.IoError,
                                e.Message);
                        }
                        if (!string.Equals(
                            Sha256Hex(current),
                            expectedBytesHash,
                            StringComparison.Ordinal))
                        {
                            return new GraphJsonPublishResult(
                                GraphJsonPublishStatus.ChangedSinceRead, null);
                        }
                        try
                        {
                            beforeReplace?.Invoke(path);
                            File.Replace(tempPath, path, null);
                            return new GraphJsonPublishResult(
                                GraphJsonPublishStatus.Published, null);
                        }
                        catch (Exception e) when (
                            e is FileNotFoundException ||
                            e is DirectoryNotFoundException)
                        {
                            // The destination vanished between the
                            // re-verification and the replace — a change to
                            // refuse on, not an I/O fault.
                            return new GraphJsonPublishResult(
                                GraphJsonPublishStatus.ChangedSinceRead,
                                e.Message);
                        }
                        catch (Exception e) when (IsTransientReplaceFailure(e))
                        {
                            // A transient hold of the destination — a
                            // scanner or importer briefly holding the file —
                            // is retried within the shared bounded budget;
                            // the next attempt re-verifies the bytes before
                            // replacing, so a hold that releases into new
                            // content is refused rather than overwritten.
                            // When the budget is used up the failure
                            // propagates and the publish fails closed.
                            if (!retry.Wait())
                            {
                                throw;
                            }
                            // A 1175 failure left the replacement file's
                            // contents unproven: the next attempt may run
                            // only once it provably holds the intended
                            // bytes again.
                            if (IsUnableToRemoveReplaced(e))
                            {
                                VerifyReplacementBytes(tempPath, output.Bytes);
                            }
                        }
                    }
                }
                finally
                {
                    DeleteTemp(tempPath);
                }
            }
            catch (Exception e) when (IsIoError(e))
            {
                return new GraphJsonPublishResult(
                    GraphJsonPublishStatus.IoError, e.Message);
            }
        }

        // Writes a destination that was absent at the call's read through a
        // same-directory temp file and File.Move so the read-to-write
        // window never leaves a torn document (SPEC §0.6 SG-17); File.Move
        // performs no overwrite — a file planted in between is never
        // replaced.
        private static void WriteAtomicCreate(string path, byte[] bytes)
        {
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteTemp(tempPath, bytes);
                File.Move(tempPath, path);
            }
            finally
            {
                DeleteTemp(tempPath);
            }
        }

        // Replaces the destination through a same-directory temp file and
        // File.Replace, re-verifying the destination's bytes against
        // expectedBytesHash on every attempt immediately before the
        // replace: a changed or vanished destination is an external change
        // to refuse on — it is left untouched and the refusal result
        // returned — never overwritten or recreated. A transient hold on
        // the destination (IsTransientReplaceFailure) is retried within the
        // shared bounded budget, then the failure propagates to fail
        // closed. beforeReplace is the test seam invoked on every attempt
        // after re-verification, immediately before the replace. Returns
        // null when the replace wrote.
        private static GraphJsonSaveResult? WriteAtomicReplace(
            string path,
            byte[] bytes,
            string expectedBytesHash,
            Action<string>? beforeReplace)
        {
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteTemp(tempPath, bytes);
                var retry = new ReplaceRetry();
                for (;;)
                {
                    byte[] current;
                    try
                    {
                        current = File.ReadAllBytes(path);
                    }
                    catch (Exception e) when (IsIoError(e))
                    {
                        // A destination that vanished mid-save is an
                        // external change to refuse on; every other read
                        // failure is an I/O error for the caller's boundary.
                        if (e is FileNotFoundException ||
                            e is DirectoryNotFoundException)
                        {
                            return new GraphJsonSaveResult(
                                GraphJsonSaveStatus.ExternalModification,
                                null, null, e.Message);
                        }
                        throw;
                    }
                    if (!string.Equals(
                        Sha256Hex(current),
                        expectedBytesHash,
                        StringComparison.Ordinal))
                    {
                        // The destination changed since the save's read:
                        // refuse and leave it untouched, reporting the
                        // newly read document when it parses.
                        var changed = WorkflowSerializer.Read(DecodeUtf8(current));
                        return new GraphJsonSaveResult(
                            GraphJsonSaveStatus.ExternalModification,
                            changed.Ok ? changed.Value : null,
                            changed.Diagnostics,
                            null);
                    }
                    try
                    {
                        beforeReplace?.Invoke(path);
                        File.Replace(tempPath, path, null);
                        return null;
                    }
                    catch (Exception e) when (
                        e is FileNotFoundException ||
                        e is DirectoryNotFoundException)
                    {
                        // The destination vanished between the
                        // re-verification and the replace — an external
                        // change to refuse on, not an I/O fault.
                        return new GraphJsonSaveResult(
                            GraphJsonSaveStatus.ExternalModification,
                            null, null, e.Message);
                    }
                    catch (Exception e) when (IsTransientReplaceFailure(e))
                    {
                        // A transient hold is retried within the shared
                        // bounded budget; when the budget is used up the
                        // failure propagates and the save fails closed.
                        if (!retry.Wait())
                        {
                            throw;
                        }
                        // A 1175 failure left the replacement file's
                        // contents unproven: the next attempt may run
                        // only once it provably holds the intended
                        // bytes again.
                        if (IsUnableToRemoveReplaced(e))
                        {
                            VerifyReplacementBytes(tempPath, bytes);
                        }
                    }
                }
            }
            finally
            {
                DeleteTemp(tempPath);
            }
        }

        // The temp file's bytes are flushed to disk before the move/replace
        // publishes them, so a crash cannot expose an unwritten stub.
        private static void WriteTemp(string tempPath, byte[] bytes)
        {
            using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static void DeleteTemp(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception e) when (IsIoError(e))
            {
                // Best-effort cleanup; a leftover temp file does not change
                // the publish's outcome.
            }
        }

        // The transient replace failures both replace paths retry under the
        // shared bounded budget: a sharing violation (Win32 error 32), a
        // lock violation (33), access denied on a held target (5) or a
        // replaced file that could not be removed (1175,
        // ERROR_UNABLE_TO_REMOVE_REPLACED — whose retry first re-proves the
        // replacement file, see VerifyReplacementBytes). An IOException
        // carries its Win32 error in the low word of its HResult under
        // FACILITY_WIN32 (0x8007xxxx) or as a bare Win32 code
        // (0x0000xxxx); a matching low word under any other facility is
        // not a Win32 error and is never retried. Error 5 also reaches
        // File.Replace as an UnauthorizedAccessException with HResult
        // 0x80070005. Every other failure — including errors 1176 and 1177
        // (ERROR_UNABLE_TO_MOVE_REPLACEMENT[2]), which can leave the
        // destination removed, renamed or carrying merged streams — is
        // not retried.
        internal static bool IsTransientReplaceFailure(Exception e)
        {
            if (e is UnauthorizedAccessException)
            {
                return e.HResult == unchecked((int)0x80070005);
            }
            if (e is IOException io)
            {
                var code = Win32ErrorCode(io.HResult);
                return code == 0x20 || code == 0x21 || code == 0x05 || code == 0x0497;
            }
            return false;
        }

        // The Win32 error an IOException's HResult encodes: the low word
        // when the facility is FACILITY_WIN32 (0x8007xxxx) or the HResult
        // is a bare Win32 code (0x0000xxxx, the form
        // new IOException(message, code) stores); -1 under any other
        // facility, where a matching low word is not a Win32 error.
        private static int Win32ErrorCode(int hresult)
        {
            var facility = hresult & unchecked((int)0xFFFF0000);
            if (facility != unchecked((int)0x80070000) && facility != 0)
            {
                return -1;
            }
            return hresult & 0xFFFF;
        }

        // True when the transient failure is Win32
        // ERROR_UNABLE_TO_REMOVE_REPLACED (1175): the one retried failure
        // after which the replacement file's contents are unproven — the
        // ReplaceFile contract preserves the files' names only.
        private static bool IsUnableToRemoveReplaced(Exception e) =>
            e is IOException io && Win32ErrorCode(io.HResult) == 0x0497;

        // Re-proves the replacement file before the attempt that follows an
        // ERROR_UNABLE_TO_REMOVE_REPLACED (1175) failure: the temp file must
        // still hold exactly the bytes this call intended to publish. This
        // implementation re-reads the temp file and re-hashes it against
        // the intended bytes (the alternative the contract allows is
        // rewriting the temp file from them). A temp file that is absent,
        // unreadable or holds different bytes can never be proven safe —
        // the thrown failure propagates through the caller's finally, which
        // removes the temp file, so the destination is never replaced from
        // an unverified replacement.
        private static void VerifyReplacementBytes(string tempPath, byte[] bytes)
        {
            var current = File.ReadAllBytes(tempPath);
            if (current.Length != bytes.Length ||
                !string.Equals(
                    Sha256Hex(current), Sha256Hex(bytes),
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    "the replacement file no longer holds the intended bytes");
            }
        }

        // One shared bounded-retry budget for a replace call: the delay
        // doubles on each failure, capped at
        // ReplaceRetryMaxDelayMilliseconds per delay, and a further attempt
        // runs only while the time since the first failed attempt plus the
        // next delay stays within ReplaceRetryBudgetMilliseconds. Wait
        // sleeps for the next delay and returns true while a further
        // attempt stays inside the budget; it returns false once the next
        // delay would pass it.
        private sealed class ReplaceRetry
        {
            private long _firstFailureAt = -1;
            private int _nextDelay = ReplaceRetryFirstDelayMilliseconds;

            internal bool Wait()
            {
                var now = s_retryClock();
                if (_firstFailureAt < 0)
                {
                    _firstFailureAt = now;
                }
                if (now - _firstFailureAt + _nextDelay > ReplaceRetryBudgetMilliseconds)
                {
                    return false;
                }
                s_retrySleep(_nextDelay);
                _nextDelay = Math.Min(_nextDelay * 2, ReplaceRetryMaxDelayMilliseconds);
                return true;
            }
        }

        // Decodes raw file bytes as UTF-8, dropping a UTF-8 BOM prefix like
        // File.ReadAllText does — callers that hash keep hashing the raw
        // bytes, BOM included.
        private static string DecodeUtf8(byte[] bytes)
        {
            var text = s_utf8.GetString(bytes);
            if (text.Length != 0 && text[0] == 0xFEFF)
            {
                text = text.Substring(1);
            }
            return text;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        // ── Non-owned member carry (byte-exact raw value text) ─────────────

        // The non-owned members the §A2 order gives fixed slots ahead of the
        // extension members; a member the writer omits is re-inserted at its
        // rank. Keys the WF model owns keep the writer's output untouched.
        private static int RankOf(string name) => name switch
        {
            "schemaVersion" => 0,
            "id" => 1,
            "title" => 2,
            "description" => 3,
            "tags" => 4,
            "inputs" => 5,
            "outputs" => 6,
            "nodes" => 7,
            "controlEdges" => 8,
            "dataEdges" => 9,
            "inputBindings" => 10,
            "layout" => 11,
            _ => 12,
        };

        private static bool IsMetadataMember(string name) =>
            string.Equals(name, "title", StringComparison.Ordinal) ||
            string.Equals(name, "description", StringComparison.Ordinal) ||
            string.Equals(name, "tags", StringComparison.Ordinal);

        private static bool IsExtensionMember(string name) =>
            name.StartsWith("x-", StringComparison.Ordinal);

        private static string CarryMetadataMembers(string writtenText, string carryText) =>
            Reassemble(writtenText, carryText, IsMetadataMember, insertCarryOnly: true);

        private static string CarryExtensionMembers(string writtenText, string carryText) =>
            Reassemble(writtenText, carryText, IsExtensionMember, insertCarryOnly: false);

        // Rebuilds the written document's member list: for every member
        // isCarried names whose carried value parses equal to the written
        // (merged) value, the carry text's raw value replaces the writer's;
        // a carried member whose value differs keeps the merged text — the
        // carry preserves bytes, it never reintroduces stale content. With
        // insertCarryOnly a carried member the written text lacks is
        // inserted at its §A2 rank, but only in the writer-omitted forms (an
        // explicit null or empty array); any other carry-only member is
        // content the merged document does not carry and is never
        // materialized. Every other member keeps its written bytes, so owned
        // content is reproduced untouched.
        private static string Reassemble(
            string writtenText,
            string carryText,
            Func<string, bool> isCarried,
            bool insertCarryOnly)
        {
            var carry = MemberMap(carryText);
            var written = ScanTopLevelMembers(writtenText);
            var parts = new List<(int Rank, int Order, string Fragment)>(written.Count + 3);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var order = 0;
            foreach (var member in written)
            {
                var writtenRaw = writtenText.Substring(
                    member.ValueStart, member.ValueEnd - member.ValueStart);
                var raw = isCarried(member.Name) &&
                    carry.TryGetValue(member.Name, out var carried) &&
                    SameValue(carried, writtenRaw)
                        ? carried
                        : writtenRaw;
                parts.Add((RankOf(member.Name), order, Fragment(member.Name, raw)));
                seen.Add(member.Name);
                order++;
            }
            if (insertCarryOnly)
            {
                foreach (var member in ScanTopLevelMembers(carryText))
                {
                    if (!isCarried(member.Name) || !seen.Add(member.Name))
                    {
                        continue;
                    }
                    var raw = carryText.Substring(
                        member.ValueStart, member.ValueEnd - member.ValueStart);
                    if (!IsWriterOmittedValue(raw))
                    {
                        continue;
                    }
                    parts.Add((RankOf(member.Name), order, Fragment(member.Name, raw)));
                    order++;
                }
            }
            parts.Sort((x, y) =>
            {
                var rank = x.Rank.CompareTo(y.Rank);
                return rank != 0 ? rank : x.Order.CompareTo(y.Order);
            });
            var builder = new StringBuilder(writtenText.Length + carryText.Length / 4 + 64);
            builder.Append("{\n");
            for (var i = 0; i < parts.Count; i++)
            {
                builder.Append(parts[i].Fragment);
                builder.Append(i + 1 < parts.Count ? ",\n" : "\n");
            }
            builder.Append("}\n");
            return builder.ToString();
        }

        private static string Fragment(string name, string rawValue) =>
            "  " + CanonicalJson.Serialize(new JValue(name)) + ": " + rawValue;

        // Parsed-value equality of two raw member values from texts that
        // parsed Ok: DeepEquals ignores formatting and member order, so
        // "A\u0062c" equals "Abc" while a stale carried value does not.
        private static bool SameValue(string rawA, string rawB) =>
            JToken.DeepEquals(JToken.Parse(rawA), JToken.Parse(rawB));

        // The writer-omitted metadata forms a re-inserted member may carry:
        // an explicit null (a null title or description the model cannot
        // tell from absent) or an explicit empty array ("tags": []). Any
        // other value is content the merged document does not carry.
        private static bool IsWriterOmittedValue(string rawValue)
        {
            var token = JToken.Parse(rawValue);
            return token.Type == JTokenType.Null ||
                (token is JArray array && array.Count == 0);
        }

        private static Dictionary<string, string> MemberMap(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in ScanTopLevelMembers(text))
            {
                if (!map.ContainsKey(member.Name))
                {
                    map.Add(member.Name, text.Substring(
                        member.ValueStart, member.ValueEnd - member.ValueStart));
                }
            }
            return map;
        }

        // A top-level member's name with its raw value span ([ValueStart,
        // ValueEnd) UTF-16 indices into the scanned text). The scanned
        // object is already known to parse strictly — the §A3 writer's
        // output or a §A3-read document — so the scanner assumes valid
        // grammar.
        private sealed class MemberSpan
        {
            internal MemberSpan(string name, int valueStart, int valueEnd)
            {
                Name = name;
                ValueStart = valueStart;
                ValueEnd = valueEnd;
            }

            internal string Name { get; }
            internal int ValueStart { get; }
            internal int ValueEnd { get; }
        }

        private static List<MemberSpan> ScanTopLevelMembers(string text)
        {
            var spans = new List<MemberSpan>();
            var i = 0;
            SkipWhitespace(text, ref i);
            if (i >= text.Length || text[i] != '{')
            {
                return spans;
            }
            i++;
            SkipWhitespace(text, ref i);
            while (i < text.Length && text[i] != '}')
            {
                var name = ScanString(text, ref i);
                SkipWhitespace(text, ref i);
                if (i < text.Length && text[i] == ':')
                {
                    i++;
                }
                SkipWhitespace(text, ref i);
                var valueStart = i;
                ScanValue(text, ref i);
                spans.Add(new MemberSpan(name, valueStart, i));
                SkipWhitespace(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    SkipWhitespace(text, ref i);
                }
            }
            return spans;
        }

        // Scans one value starting at text[i]; i ends just past it.
        private static void ScanValue(string text, ref int i)
        {
            if (i >= text.Length)
            {
                return;
            }
            var c = text[i];
            if (c == '{' || c == '[')
            {
                var depth = 0;
                while (i < text.Length)
                {
                    var ch = text[i];
                    if (ch == '"')
                    {
                        ScanString(text, ref i);
                        continue;
                    }
                    if (ch == '{' || ch == '[')
                    {
                        depth++;
                        i++;
                        continue;
                    }
                    if (ch == '}' || ch == ']')
                    {
                        depth--;
                        i++;
                        if (depth == 0)
                        {
                            return;
                        }
                        continue;
                    }
                    i++;
                }
                return;
            }
            if (c == '"')
            {
                ScanString(text, ref i);
                return;
            }
            while (i < text.Length &&
                text[i] != ',' && text[i] != '}' && text[i] != ']' &&
                text[i] != ' ' && text[i] != '\t' && text[i] != '\r' && text[i] != '\n')
            {
                i++;
            }
        }

        // Decodes the string literal at text[i] ('"'); i ends past the
        // closing quote. Only member names are decoded — values keep their
        // raw spans — so no surrogate-pair assembly is needed: a \uXXXX unit
        // is appended as the (char) it names.
        private static string ScanString(string text, ref int i)
        {
            var builder = new StringBuilder();
            i++;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '"')
                {
                    i++;
                    return builder.ToString();
                }
                if (c == '\\')
                {
                    i++;
                    if (i >= text.Length)
                    {
                        break;
                    }
                    switch (text[i])
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (i + 4 < text.Length)
                            {
                                builder.Append((char)Convert.ToInt32(
                                    text.Substring(i + 1, 4), 16));
                                i += 4;
                            }
                            break;
                    }
                    i++;
                    continue;
                }
                builder.Append(c);
                i++;
            }
            return builder.ToString();
        }

        private static void SkipWhitespace(string text, ref int i)
        {
            while (i < text.Length &&
                (text[i] == ' ' || text[i] == '\t' || text[i] == '\r' || text[i] == '\n'))
            {
                i++;
            }
        }

        private static bool IsIoError(Exception e) =>
            e is IOException ||
            e is UnauthorizedAccessException ||
            e is NotSupportedException ||
            e is System.Security.SecurityException ||
            e is ArgumentException;
    }
}
