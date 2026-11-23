#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The <see cref="IRunStore"/> implementation of SPEC §F1: an injected root
    /// directory (<c>Library/OpWeave/</c> inside Unity, any temp root outside)
    /// accessed through <see cref="System.IO"/> only, so the whole type runs
    /// without the Editor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CreateRun(string, CompiledPlan, WorkflowDocument)"/> lays out
    /// a Run exactly as SPEC §F1: <c>Runs/&lt;runId&gt;/plan.json</c> as the
    /// <see cref="PlanJsonCodec"/> Canonical JSON bytes (its UTF-8 SHA-256
    /// becomes the RunAccepted <c>planFileDigest</c> — the digest is never a
    /// plan.json member, SPEC §D7 item 6, §D0.1), <c>workflow.json</c> as the
    /// audit copy of the WF document, <c>journal.ndjson</c> holding the
    /// <see cref="JournalHeader"/> line and a Flushed RunAccepted record, the
    /// regenerable <c>state.json</c> cache, the <c>changes.json</c> slot of
    /// SPEC §F7 and the <c>index.json</c> entry. The Run's initial state
    /// follows SPEC §E2 item 1: a Plan with approvals enters
    /// <see cref="RunState.AwaitingApproval"/>, otherwise
    /// <see cref="RunState.Queued"/>.
    /// </para>
    /// <para>
    /// Durability follows SPEC §F3. The journal is opened
    /// <c>FileMode.Append</c>/<c>FileShare.Read</c> for every write; a Flushed
    /// append writes every Buffered line still pending and the new line, then
    /// waits for <c>Flush(flushToDisk: true)</c>. Buffered lines are held in
    /// memory and reach the file at the next Flushed append or the explicit
    /// <see cref="FlushPending"/> hook (the <c>beforeAssemblyReload</c> /
    /// <c>quitting</c> sinks the Unity wiring registers). The effective
    /// durability of an append is never below the SPEC §F2 table class of the
    /// record's type: a Flushed-table record is fsync'd even when the caller
    /// asks for Buffered. <c>state.json</c>, <c>index.json</c> and
    /// <c>changes.json</c> go through a same-directory temp file fsync'd and
    /// atomically replaced (SPEC §F3 item 2); a transient Win32 sharing
    /// failure of the replace step — an indexer or virus scanner briefly
    /// holding a file — is retried a bounded number of times first. Every
    /// other write failure — including
    /// a journal or run directory that should exist but does not — returns
    /// OW-STORE-001 (the SPEC §F1 error); the Run-Failed consequence belongs to
    /// the caller. Before a record is written, a journal tail the load path
    /// would drop (the SPEC §F3 item 3 broken last line, torn or
    /// newline-terminated) is truncated back to the last complete line so
    /// the new record never glues onto it,
    /// and once the journal write has succeeded a state.json/index.json
    /// maintenance failure is a non-fatal warning rather than OW-STORE-001 —
    /// <see cref="Load"/> regenerates both caches from the journal (SPEC §F1).
    /// </para>
    /// <para>
    /// The SPEC §F1 signature of <see cref="IRunStore.CreateRun"/> carries no
    /// actor, yet the §F2 RunAccepted data lists <c>actor</c> as main content;
    /// the <see cref="CreateRun(string, CompiledPlan, WorkflowDocument, ActorKind)"/>
    /// overload records it (the RunCoordinator of SPEC §E2 knows the
    /// <see cref="ActorKind"/>), while the interface method writes the
    /// remaining members — <c>planFileDigest</c> is the only §F2-required one.
    /// </para>
    /// <para>
    /// <see cref="Load"/> reconstructs a Run only from its journal — the
    /// single truth — after the SPEC §D0.1/§D7 item 6 verification: the
    /// RunAccepted <c>planFileDigest</c> against the actual plan.json bytes,
    /// the plan's format/canonicalJson versions (which the readers pin to the
    /// same known constants the JournalHeader is checked against, so a
    /// mismatch can never decode), and the recomputed
    /// <see cref="CompiledPlan.PlanHash"/> against the header's. A broken
    /// last journal line — the SPEC §F3 item 3 interrupted-write tail:
    /// bytes that cannot be read back as a record envelope at all
    /// (undecodable UTF-8, unparseable JSON or a damaged member — the
    /// missing or non-boolean <c>required</c> of SPEC §F2 item 2 included)
    /// — is dropped with an OW-STORE-002 warning and the "F5 decision
    /// required" flag of <see cref="RunSnapshot"/>; a corrupt middle
    /// line, a complete last record failing integrity (a seq gap or
    /// duplicate, a crc mismatch), an unknown required record, an unknown
    /// version, a missing RunAccepted or planFileDigest and any plan.json
    /// byte change are OW-STORE-002 and NeedsReview — never an automatic
    /// success. A missing
    /// or corrupt <c>state.json</c>/<c>index.json</c> is regenerated from
    /// the journal.
    /// Core runs on the main thread only, so no synchronization is taken.
    /// </para>
    /// </remarks>
    public sealed class FileRunStore : IRunStore
    {
        private const string RunsDirectoryName = "Runs";
        private const string IndexFileName = "index.json";
        private const string PlanFileName = "plan.json";
        private const string WorkflowFileName = "workflow.json";
        private const string JournalFileName = "journal.ndjson";
        private const string StateFileName = "state.json";
        private const string ChangesFileName = "changes.json";
        private const string TempSuffix = ".opweave-tmp";
        private const int MaxReplaceAttempts = 5;
        private const int ReplaceRetryDelayMilliseconds = 15;

        private const string RunsMember = "runs";
        private const string RunIdMember = "runId";
        private const string WorkflowIdMember = "workflowId";
        private const string StateMember = "state";
        private const string CreatedAtMember = "createdAt";
        private const string UpdatedAtMember = "updatedAt";
        private const string ActorMember = "actor";
        private const string ApprovalsMember = "approvals";
        private const string PlanFileDigestMember = "planFileDigest";
        private const string NodeIdMember = "nodeId";
        private const string EffectMember = "effect";
        private const string DetailMember = "detail";

        private const string UlidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const int UlidLength = 26;

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);
        private static readonly UTF8Encoding s_planUtf8 = new UTF8Encoding(false, false);

        private readonly string _root;
        private readonly string _session;

        // Buffered journal lines not yet forced to disk, keyed by runId and
        // held in append order (SPEC §F3 item 1).
        private readonly Dictionary<string, List<string>> _pending =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Per-run journal fold used to keep the state.json/index.json caches
        // current on Append; seeded at CreateRun, lazily rebuilt from the
        // journal for runs this store instance did not create, and refreshed
        // by Load.
        private readonly Dictionary<string, JournalFold> _fold =
            new Dictionary<string, JournalFold>(StringComparer.Ordinal);

        // Reentrancy guard for RebuildIndex: every Load inside the scan
        // refreshes the caches, which reaches UpdateIndex while the index
        // file is still missing or unreadable; the guard makes that nested
        // rebuild a no-op so the outer scan's complete index is never
        // preempted by a partial one.
        private bool _indexRebuildInProgress;

        /// <summary>
        /// A store rooted at <paramref name="rootPath"/> (the Unity wiring uses
        /// the project's <c>Library/OpWeave/</c>) writing journal records under
        /// the Editor session id <paramref name="sessionId"/>
        /// (<c>SessionState</c>'s GUID, SPEC §F2, §F4 item 4).
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="rootPath"/> or <paramref name="sessionId"/> is null
        /// or empty.
        /// </exception>
        public FileRunStore(string rootPath, string sessionId)
        {
            if (string.IsNullOrEmpty(rootPath))
            {
                throw new ArgumentException(
                    "The Run Store root path must not be null or empty.", nameof(rootPath));
            }
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException(
                    "The Editor session id must not be null or empty.", nameof(sessionId));
            }
            _root = rootPath;
            _session = sessionId;
        }

        /// <inheritdoc cref="IRunStore.CreateRun"/>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="plan"/> or <paramref name="doc"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="runId"/> is not a <c>"run_" + ULID</c> identifier
        /// (SPEC §0.4) or <paramref name="plan"/> is outside the plan.json
        /// contract of <see cref="PlanJsonCodec.Write"/>.
        /// </exception>
        public Result<Unit> CreateRun(string runId, CompiledPlan plan, WorkflowDocument doc) =>
            CreateRun(runId, plan, doc, actor: null);

        /// <summary>
        /// <see cref="CreateRun(string, CompiledPlan, WorkflowDocument)"/> that
        /// also records the accepting <paramref name="actor"/> in the
        /// RunAccepted data (SPEC §F2 table). Internal: the §F1 public surface
        /// carries no actor; only in-assembly callers (the RunCoordinator of
        /// SPEC §E2) know the <see cref="ActorKind"/>.
        /// </summary>
        /// <inheritdoc cref="CreateRun(string, CompiledPlan, WorkflowDocument)"/>
        internal Result<Unit> CreateRun(
            string runId, CompiledPlan plan, WorkflowDocument doc, ActorKind actor) =>
            CreateRun(runId, plan, doc, (ActorKind?)actor);

        /// <inheritdoc cref="IRunStore.Append"/>
        /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="runId"/> is not a <c>"run_" + ULID</c> identifier or
        /// <paramref name="record"/> is outside the v1 journal contract of
        /// <see cref="JournalCodec.WriteRecord"/>.
        /// </exception>
        public Result<Unit> Append(string runId, JournalRecord record, Durability durability)
        {
            if (record is null)
            {
                throw new ArgumentNullException(nameof(record));
            }
            RequireRunId(runId);
            var line = JournalCodec.WriteRecord(record);
            var journal = JournalPath(runId);
            // The SPEC §F2 table class is a floor: a Flushed-table record is
            // always fsync'd; Buffered may only downgrade records the table
            // itself marks Buffered.
            var flushed = durability == Durability.Flushed ||
                record.Durability == Durability.Flushed;
            try
            {
                if (!File.Exists(journal))
                {
                    return StoreFailure(
                        "The journal file of the Run does not exist.");
                }
                var fold = FoldFor(runId);
                var stateBefore = fold.State;
                if (flushed)
                {
                    WriteJournalLines(journal, runId, line);
                }
                else
                {
                    PendingLines(runId).Add(line);
                }
                // state.json and index.json are caches over the journal: a
                // record that changes the folded Run state rewrites both
                // through the SPEC §F3 item 2 atomic replace. Only
                // Flushed-table records change the state, so a cache write
                // can never race ahead of the journal line that causes it.
                if (fold.Apply(record) && fold.State != stateBefore)
                {
                    // The journal write above is authoritative — the record is
                    // committed on disk, or queued in _pending as part of the
                    // journal's truth — while both files are caches Load
                    // rebuilds from the journal (SPEC §F1). A maintenance
                    // failure here is a non-fatal warning, never "the journal
                    // record could not be appended": that would invite a
                    // retry duplicating the seq.
                    try
                    {
                        var now = UtcTimestamp.Now();
                        ReplaceAtomically(
                            Path.Combine(RunDirectory(runId), StateFileName),
                            StateJsonBytes(runId, fold.State, now));
                        UpdateIndex(runId, null, fold.State, now);
                    }
                    catch (Exception e) when (IsIoFailure(e))
                    {
                        return Result<Unit>.Success(
                            Unit.Value,
                            ErrorCodes.Create(
                                ErrorCodes.OW_STORE_001,
                                "The Run state caches could not be updated; the next Load rebuilds them.",
                                severity: Severity.Warning));
                    }
                }
                return Result<Unit>.Success(Unit.Value);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return StoreFailure("The journal record could not be appended.");
            }
        }

        /// <summary>
        /// The explicit flush hook of SPEC §F3 item 1: every Buffered journal
        /// line still pending — in every Run — is appended and forced to disk.
        /// The Unity wiring calls this at <c>beforeAssemblyReload</c> and
        /// <c>quitting</c>. A run whose journal has vanished reports
        /// OW-STORE-001 but does not stop the other runs' flush.
        /// </summary>
        public Result<Unit> FlushPending()
        {
            Diagnostic? first = null;
            foreach (var runId in new List<string>(_pending.Keys))
            {
                try
                {
                    var journal = JournalPath(runId);
                    if (!File.Exists(journal))
                    {
                        first ??= StoreDiagnostic(
                            "The journal file of the Run does not exist.");
                        continue;
                    }
                    WriteJournalLines(journal, runId, extraLine: null);
                }
                catch (Exception e) when (IsIoFailure(e))
                {
                    first ??= StoreDiagnostic(
                        "A buffered journal line could not be flushed.");
                }
            }
            return first is null
                ? Result<Unit>.Success(Unit.Value)
                : Result<Unit>.Failure(first);
        }

        /// <inheritdoc cref="IRunStore.Load"/>
        /// <exception cref="ArgumentException">
        /// <paramref name="runId"/> is not a <c>"run_" + ULID</c> identifier
        /// (SPEC §0.4).
        /// </exception>
        public Result<RunSnapshot> Load(string runId) =>
            Load(runId, maintainCaches: true);

        // The load behind the tombstone-aware public one: the deletion
        // tombstones are consulted before the Run directory, so a
        // tombstoned Run answers OW-STORE-003 even while an interrupted
        // deletion still leaves its directory behind, and a tombstone
        // file that cannot prove its contents is OW-STORE-002 — never
        // OW-RUN-010, which would silently reclassify a deleted Run as
        // never-created (SPEC §0.6 SG-16). The deletion path's own
        // terminal check passes maintainCaches: false — a Run about to
        // be removed (or refused) does not get its caches rewritten
        // first.
        private Result<RunSnapshot> Load(string runId, bool maintainCaches)
        {
            RequireRunId(runId);
            var tombstones = ReadTombstoneIds();
            if (!tombstones.Ok)
            {
                return Result<RunSnapshot>.Failure(tombstones.Diagnostics);
            }
            if (tombstones.Value!.Contains(runId))
            {
                return Result<RunSnapshot>.Failure(DeletedRunDiagnostic());
            }
            var runDir = RunDirectory(runId);
            if (!Directory.Exists(runDir))
            {
                return Result<RunSnapshot>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_010, "The Run does not exist."));
            }

            // Buffered lines of this store instance belong to the journal's
            // truth, so they are forced to disk before the read (SPEC §F3
            // item 1). A flush failure leaves the on-disk truth unchanged.
            var journal = JournalPath(runId);
            if (_pending.TryGetValue(runId, out var held) && held.Count != 0)
            {
                try
                {
                    WriteJournalLines(journal, runId, extraLine: null);
                }
                catch (Exception e) when (IsIoFailure(e))
                {
                }
            }

            var diagnostics = new List<Diagnostic>();
            var fold = JournalFold.Empty();
            JournalHeader? header = null;
            JournalRecord? accepted = null;
            var review = false;
            string? reviewReason = null;

            var lines = ReadJournalLines(journal);
            if (lines is null || lines.Count == 0 || lines[0] is null)
            {
                review = true;
                reviewReason = "The Run journal file is missing, unreadable or has no readable header line.";
            }
            else
            {
                var headerRead = JournalCodec.ReadHeader(lines[0]!);
                if (headerRead.Status != JournalLineStatus.Decoded)
                {
                    // An unknown or unreadable header: the records are never
                    // interpreted (SPEC §F2 item 4).
                    review = true;
                    reviewReason = headerRead.Diagnostic!.Message;
                }
                else if (!string.Equals(
                             headerRead.Header!.RunId, runId, StringComparison.Ordinal))
                {
                    review = true;
                    reviewReason = "The journal header runId does not match the Run.";
                }
                else
                {
                    header = headerRead.Header;
                    FoldJournalLines(
                        lines, fold, diagnostics, ref review, ref reviewReason, ref accepted);
                }
            }

            // Integrity verification (SPEC §D0.1, §F2, §D7 item 6), in order:
            // planFileDigest → plan versions (JournalHeader equality included)
            // → recomputed planHash. Every failure is OW-STORE-002 NeedsReview.
            CompiledPlan? plan = null;
            byte[]? planBytes = null;
            var planPath = Path.Combine(runDir, PlanFileName);
            try
            {
                if (File.Exists(planPath))
                {
                    planBytes = File.ReadAllBytes(planPath);
                }
            }
            catch (Exception e) when (IsIoFailure(e))
            {
            }
            if (!review)
            {
                if (accepted is null)
                {
                    review = true;
                    reviewReason = "The journal has no RunAccepted record.";
                }
                else if (PlanFileDigestOf(accepted) is not { } expectedDigest)
                {
                    review = true;
                    reviewReason = "The RunAccepted record has no planFileDigest.";
                }
                else if (planBytes is null)
                {
                    review = true;
                    reviewReason = "The Run plan file is missing or unreadable.";
                }
                else if (!string.Equals(
                             Sha256LowerHex(planBytes), expectedDigest,
                             StringComparison.Ordinal))
                {
                    review = true;
                    reviewReason =
                        "The plan file bytes do not match the recorded planFileDigest.";
                }
            }
            if (planBytes is not null)
            {
                // Read with replacement fallback: bytes that are not valid
                // UTF-8 surface as a Corrupt plan, not an exception.
                var planRead = PlanJsonCodec.Read(s_planUtf8.GetString(planBytes));
                if (planRead.Status == PlanJsonStatus.Decoded)
                {
                    plan = planRead.Plan;
                }
                else if (!review)
                {
                    review = true;
                    reviewReason = planRead.Diagnostic!.Message;
                }
            }
            if (!review && plan is not null &&
                !string.Equals(plan.PlanHash, header!.PlanHash, StringComparison.Ordinal))
            {
                review = true;
                reviewReason = "The plan file planHash does not match the journal header.";
            }

            var state = review ? RunState.NeedsReview : fold.State;
            if (review)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_002, reviewReason!));
            }
            var snapshot = new RunSnapshot(
                runId, plan, state, fold.Steps(), fold.Outputs, fold.OpenResources,
                fold.CancelPending, fold.RecoveryDecisionRequired,
                fold.CreatedAt, fold.UpdatedAt, fold.Session, fold.LastSeq,
                fold.Ended, fold.LastStepIndex, fold.ReviewRetryStep,
                fold.CancelReason, fold.CreationSession);
            _fold[runId] = fold;
            if (maintainCaches)
            {
                RefreshCaches(runId, plan?.WorkflowId, state);
            }
            return diagnostics.Count == 0
                ? Result<RunSnapshot>.Success(snapshot)
                : Result<RunSnapshot>.Success(snapshot, diagnostics);
        }

        /// <inheritdoc cref="IRunStore.List"/>
        /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
        public Result<IReadOnlyList<RunIndexEntry>> List(RunQuery query)
        {
            if (query is null)
            {
                throw new ArgumentNullException(nameof(query));
            }
            // index.json is a cache over the journals (SPEC §F1/§F3), never
            // the source of a result: a readable index can still report a
            // stale state — the journal record committed and the cache
            // update failed or was interrupted — and a state filter over
            // such a cache would hide the very Runs recovery must find.
            // Membership and state are therefore derived from the journals
            // on every call through the same Load/fold path the rebuild
            // uses — a missing or corrupt journal already classifies
            // NeedsReview — the query filters those derived entries, and
            // index.json is rewritten as a cache only when it differs from
            // the derived truth. The persisted set is established first and
            // the scan fails closed: when it cannot be enumerated, List
            // reports OW-STORE-001 and index.json is left untouched rather
            // than rewritten from a membership that was never proven.
            // The SG-16 tombstones are read first and fail closed for the
            // same reason: a corrupt tombstone file degrades every per-Run
            // Load into the OW-STORE-002 the derivation silently skips, so
            // without this check List would read as an empty store while
            // the Runs still exist. Tombstoned ids are removed from the
            // derived set — a deletion tombstone wins over the Run
            // directory it names (an interrupted deletion leaves both).
            var tombstones = ReadTombstoneIds();
            if (!tombstones.Ok)
            {
                return Result<IReadOnlyList<RunIndexEntry>>.Failure(
                    tombstones.Diagnostics);
            }
            var persisted = PersistedRunIds();
            if (!persisted.Ok)
            {
                return Result<IReadOnlyList<RunIndexEntry>>.Failure(
                    persisted.Diagnostics);
            }
            var live = new List<string>(persisted.Value!.Count);
            foreach (var runId in persisted.Value!)
            {
                if (!tombstones.Value!.Contains(runId))
                {
                    live.Add(runId);
                }
            }
            var entries = DeriveIndexEntries(live);
            var filtered = entries;
            if (query.States is { Count: > 0 } states)
            {
                var wanted = new HashSet<RunState>(states);
                filtered = new List<RunIndexEntry>(entries.Count);
                foreach (var entry in entries)
                {
                    if (wanted.Contains(entry.State))
                    {
                        filtered.Add(entry);
                    }
                }
            }
            WriteIndexCache(entries);
            return Result<IReadOnlyList<RunIndexEntry>>.Success(filtered);
        }

        /// <summary>
        /// The physical deletion of a finished Run — the
        /// <paramref name="delete"/> delegate the SPEC §F8 retention pass
        /// selects candidates for — implemented as the SG-16 tombstone
        /// transaction. The whole sequence runs inside the Run Store
        /// inter-process exclusion (<see cref="CoordinatorRunLock"/>) so
        /// no other Writer — Editor, CLI or test process — can interleave
        /// between the tombstone commit and the removal, and nothing is
        /// destroyed before the tombstone is durable:
        /// </summary>
        /// <list type="number">
        /// <item>the SG-16 tombstone file must first prove its contents
        /// (a corrupt one is OW-STORE-002 and nothing is touched),</item>
        /// <item>a Run already tombstoned is the idempotent re-delete:
        /// only the leftover physical removal is finished,</item>
        /// <item>an unknown runId stays OW-RUN-010,</item>
        /// <item>SPEC §F8's terminal check goes through the verified
        /// journal/plan Load — never a cache — so an unfinished or
        /// damaged (NeedsReview) Run is refused as OW-STORE-001
        /// untouched,</item>
        /// <item>the <c>deleted-runs.ndjson</c> tombstone is appended,
        /// fsync'd and durably published,</item>
        /// <item>the committed tombstone's durability is re-established
        /// at the point of reliance
        /// (<see cref="DeletedRuns.EnsureDurable"/>) — only then</item>
        /// <item>the Run directory and its index entry are removed.</item>
        /// </list>
        /// <remarks>
        /// An interruption between the fsync'd tombstone and the
        /// directory removal leaves a provably deleted Run behind:
        /// every read keeps answering OW-STORE-003 and the next
        /// <see cref="CompleteInterruptedDeletions"/> sweep or re-delete
        /// finishes the removal — the deleted-vs-never-created
        /// distinction can never be lost. A tombstone append that fails
        /// is OW-STORE-001 with the Run fully intact; a directory
        /// removal that fails after the tombstone committed is also
        /// OW-STORE-001, and the idempotent retry completes it. A
        /// visible record is not by itself a durable one — the POSIX
        /// publish lands the directory entry before its fsync can
        /// complete — so every removal path, the idempotent re-delete
        /// and the sweep included, re-establishes the committed file's
        /// durability immediately before it destroys anything.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// <paramref name="runId"/> is not a <c>"run_" + ULID</c> identifier
        /// (SPEC §0.4).
        /// </exception>
        public Result<Unit> Delete(string runId) =>
            Delete(runId, DeletedRuns.ReasonRetention);

        /// <summary>
        /// The SG-16 tombstone transaction behind
        /// <see cref="Delete(string)"/> with an explicit tombstone
        /// <paramref name="reason"/> — <c>retention</c> for the §F8 pass,
        /// <c>manual</c> for an operator-initiated deletion. Internal:
        /// the public §F1 surface deletes only for retention.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="runId"/> is not a <c>"run_" + ULID</c> identifier
        /// (SPEC §0.4) or <paramref name="reason"/> is not a known SG-16
        /// deletion reason.
        /// </exception>
        internal Result<Unit> Delete(string runId, string reason)
        {
            RequireRunId(runId);
            if (!DeletedRuns.IsKnownReason(reason))
            {
                throw new ArgumentException(
                    "The deletion reason must be one of the SG-16 tombstone reasons.",
                    nameof(reason));
            }
            var acquired = CoordinatorRunLock.TryAcquire(_root);
            if (!acquired.Ok || acquired.Value is not { } held)
            {
                return Result<Unit>.Failure(acquired.Diagnostics);
            }
            try
            {
                var tombstones = ReadTombstoneIds();
                if (!tombstones.Ok)
                {
                    // Fail closed: deleting the directory while the
                    // tombstone file cannot be proven would lose the
                    // deleted-vs-never-created distinction it exists to
                    // preserve.
                    return Result<Unit>.Failure(tombstones.Diagnostics);
                }
                if (tombstones.Value!.Contains(runId))
                {
                    // The tombstone already proves the Run logically
                    // deleted; the idempotent re-delete only finishes
                    // an interrupted physical removal. The record's
                    // visibility does not prove its durability — an
                    // earlier publish can have landed the directory
                    // entry while its sync failed — so the committed
                    // file is made durable at the point of reliance
                    // first, and a failure removes nothing.
                    var ensured = EnsureTombstoneDurable();
                    if (!ensured.Ok)
                    {
                        return ensured;
                    }
                    return CompleteDeletion(runId);
                }
                if (!Directory.Exists(RunDirectory(runId)))
                {
                    return Result<Unit>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_RUN_010, "The Run does not exist."));
                }
                // SPEC §F8: unfinished and NeedsReview Runs are never
                // deleted. The check goes through the verified Load —
                // journal plus planFileDigest/planHash — never a cache,
                // so a damaged Run classifies NeedsReview and is
                // refused. maintainCaches: false: a refusal must leave
                // index.json and state.json byte-identical, and a
                // deletion removes whatever it would have written.
                var load = Load(runId, maintainCaches: false);
                if (!load.Ok || !IsTerminalState(load.Value!.State))
                {
                    return StoreFailure(
                        "The Run is not finished and must not be deleted.");
                }
                // The tombstone commits before any destructive step —
                // append plus fsync plus the durable publish precedes
                // the directory and index removal, so an interruption
                // keeps the Run provably deleted and a later sweep or
                // re-delete finishes the removal instead of resurrecting
                // it as unknown.
                var tombstoned = AppendTombstone(runId, reason);
                if (!tombstoned.Ok)
                {
                    return tombstoned;
                }
                // The destructive half relies on the committed
                // tombstone: its durability is re-established here, at
                // the point of reliance, not assumed from the commit
                // having once returned success.
                var durable = EnsureTombstoneDurable();
                if (!durable.Ok)
                {
                    return durable;
                }
                return CompleteDeletion(runId);
            }
            finally
            {
                held.Dispose();
            }
        }

        /// <summary>
        /// Answers whether <paramref name="runId"/> is recorded in the
        /// SG-16 deletion tombstones — the Core-owned lookup the AgentApi
        /// observation readers use to surface OW-STORE-003 for a deleted
        /// Run instead of the OW-RUN-010 of a never-created one, without
        /// any tombstone copy of their own. A tombstone file that cannot
        /// prove its contents is OW-STORE-002.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="runId"/> is not a <c>"run_" + ULID</c> identifier
        /// (SPEC §0.4).
        /// </exception>
        internal Result<bool> IsDeletedRun(string runId)
        {
            RequireRunId(runId);
            var tombstones = ReadTombstoneIds();
            return tombstones.Ok
                ? Result<bool>.Success(tombstones.Value!.Contains(runId))
                : Result<bool>.Failure(tombstones.Diagnostics);
        }

        /// <summary>
        /// The restart/recovery sweep of the SG-16 deletion contract:
        /// under the Run Store exclusion, every tombstoned Run whose
        /// directory or index entry survived an interrupted deletion is
        /// physically removed — the tombstone already proves the Run
        /// logically deleted, so the sweep finishes only the destructive
        /// half. A tombstone file that cannot be proven intact is
        /// OW-STORE-002 and nothing is removed; one failed removal does
        /// not stop the other sweeps and reports its diagnostic. The
        /// committed file's durability is re-established at the point of
        /// reliance (<see cref="DeletedRuns.EnsureDurable"/>) before any
        /// removal — the sweep never trusts a merely visible record.
        /// </summary>
        internal Result<Unit> CompleteInterruptedDeletions()
        {
            var acquired = CoordinatorRunLock.TryAcquire(_root);
            if (!acquired.Ok || acquired.Value is not { } held)
            {
                return Result<Unit>.Failure(acquired.Diagnostics);
            }
            try
            {
                var tombstones = ReadTombstoneIds();
                if (!tombstones.Ok)
                {
                    return Result<Unit>.Failure(tombstones.Diagnostics);
                }
                // Every removal below relies on the committed tombstone
                // file; a visible-but-undurable publish (the POSIX
                // directory-sync window) is made durable at the point of
                // reliance — one re-publish of the shared file covers
                // every entry — or nothing is removed.
                if (tombstones.Value!.Count != 0)
                {
                    var durable = EnsureTombstoneDurable();
                    if (!durable.Ok)
                    {
                        return Result<Unit>.Failure(durable.Diagnostics);
                    }
                }
                var diagnostics = new List<Diagnostic>();
                var failed = false;
                foreach (var runId in tombstones.Value!)
                {
                    var completed = CompleteDeletion(runId);
                    diagnostics.AddRange(completed.Diagnostics);
                    failed |= !completed.Ok;
                }
                return failed
                    ? Result<Unit>.Failure(diagnostics)
                    : Result<Unit>.Success(Unit.Value, diagnostics);
            }
            finally
            {
                held.Dispose();
            }
        }

        // The destructive half of the tombstone transaction — the Run
        // directory and its index entry — run only under the held
        // exclusion and only once the tombstone is durably committed;
        // also the idempotent completion of an interrupted deletion. A
        // directory that raced away is still success (the Run is already
        // provably deleted); an index-entry removal failure is a
        // non-fatal warning — index.json is a cache the next List
        // rebuild drops the entry from anyway.
        private Result<Unit> CompleteDeletion(string runId)
        {
            try
            {
                var runDir = RunDirectory(runId);
                if (Directory.Exists(runDir))
                {
                    Directory.Delete(runDir, recursive: true);
                }
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return StoreFailure("The Run directory could not be deleted.");
            }
            _pending.Remove(runId);
            _fold.Remove(runId);
            try
            {
                RemoveIndexEntry(runId);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Result<Unit>.Success(Unit.Value, ErrorCodes.Create(
                    ErrorCodes.OW_STORE_001,
                    "The index entry of the deleted Run could not be removed; the next List rebuild drops it.",
                    severity: Severity.Warning));
            }
            return Result<Unit>.Success(Unit.Value);
        }

        // Removes runId's entry from the index.json cache — a missing or
        // unreadable index has nothing to remove, and a corrupt one is
        // left for the journal-derived rebuild that drops the deleted
        // Run by itself.
        private void RemoveIndexEntry(string runId)
        {
            var path = Path.Combine(_root, IndexFileName);
            if (ReadCacheJson(path) is not JObject existing ||
                existing[RunsMember] is not JArray runs)
            {
                return;
            }
            JObject? previous = null;
            foreach (var item in runs)
            {
                if (!TryReadIndexEntry(item, out var entry))
                {
                    return;
                }
                if (string.Equals(entry!.RunId, runId, StringComparison.Ordinal))
                {
                    previous = (JObject)item;
                }
            }
            if (previous is null)
            {
                return;
            }
            runs.Remove(previous);
            ReplaceAtomically(path, s_utf8.GetBytes(CanonicalJson.Serialize(existing)));
        }

        private string TombstonePath() =>
            Path.Combine(_root, DeletedRuns.FileName);

        private Result<HashSet<string>> ReadTombstoneIds() =>
            DeletedRuns.ReadIds(TombstonePath());

        private Result<Unit> AppendTombstone(string runId, string reason) =>
            DeletedRuns.Append(
                TombstonePath(), runId, reason,
                TombstoneStreamWrapper, TombstonePublishStep);

        // The point-of-reliance durability check of the SG-16 removal
        // paths: re-publishes the current committed tombstone file —
        // content and directory entry durable on every platform — so no
        // removal ever relies on a record that is only visible. Under
        // the held exclusion the file cannot change between this and
        // the destructive step.
        private Result<Unit> EnsureTombstoneDurable() =>
            DeletedRuns.EnsureDurable(
                TombstonePath(), TombstoneStreamWrapper, TombstonePublishStep);

        private static bool IsTerminalState(RunState state) =>
            state is RunState.Succeeded or RunState.Failed or RunState.Cancelled;

        private static Diagnostic DeletedRunDiagnostic() =>
            ErrorCodes.Create(
                ErrorCodes.OW_STORE_003, "The Run was deleted.");

        /// <summary>
        /// The persisted size of one Run in bytes — the sum of every file
        /// under its <c>Runs/&lt;runId&gt;/</c> directory — the
        /// <paramref name="measureRunSize"/> input of the SPEC §F8
        /// retention pass (<see cref="OpWeave.Core.Persistence.RunRetention"/>).
        /// An unknown <paramref name="runId"/> is OW-RUN-010; a read that
        /// fails (enumeration, metadata, a directory racing away mid-scan)
        /// is OW-STORE-001 — the caller decides the direction to fail.
        /// Internal: the Run Store's size metric is the retention pass's
        /// input, not a public contract of SPEC §F1.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="runId"/> is not a <c>"run_" + ULID</c> identifier
        /// (SPEC §0.4).
        /// </exception>
        internal Result<long> MeasureRunBytes(string runId)
        {
            RequireRunId(runId);
            var runDir = RunDirectory(runId);
            try
            {
                if (!Directory.Exists(runDir))
                {
                    return Result<long>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_RUN_010, "The Run does not exist."));
                }
                long total = 0;
                foreach (var file in Directory.EnumerateFiles(
                             runDir, "*", SearchOption.AllDirectories))
                {
                    total += new FileInfo(file).Length;
                }
                return Result<long>.Success(total);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Result<long>.Failure(StoreDiagnostic(
                    "The Run size could not be measured."));
            }
        }

        // Reads journal.ndjson as raw bytes split at '\n' so a half-written
        // last line reaches the codec instead of a decoding exception: a
        // segment that is not valid UTF-8 is a null line, which the caller
        // treats as a corrupt line (droppable only as the last one, SPEC §F3
        // item 3). Returns null when the file is missing or unreadable.
        private static List<string?>? ReadJournalLines(string journal)
        {
            byte[] raw;
            try
            {
                if (!File.Exists(journal))
                {
                    return null;
                }
                raw = File.ReadAllBytes(journal);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return null;
            }
            var lines = new List<string?>();
            var start = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] == (byte)'\n')
                {
                    lines.Add(DecodeLine(raw, start, i - start));
                    start = i + 1;
                }
            }
            if (start < raw.Length)
            {
                lines.Add(DecodeLine(raw, start, raw.Length - start));
            }
            return lines;
        }

        private static string? DecodeLine(byte[] bytes, int start, int length)
        {
            if (length > 0 && bytes[start + length - 1] == (byte)'\r')
            {
                length--;
            }
            try
            {
                return s_utf8.GetString(bytes, start, length);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        // The record-line loop of Load: decodes each line in sequence (every
        // line — Skipped ones included — consumes one seq position, which
        // LastSeq reflects, SPEC §F2), folds decoded records, remembers the
        // first RunAccepted, drops a broken last line with a warning and
        // marks every other failure — a complete, readable last record with
        // a seq gap or duplicate included — NeedsReview.
        private static void FoldJournalLines(
            List<string?> lines,
            JournalFold fold,
            List<Diagnostic> diagnostics,
            ref bool review,
            ref string? reviewReason,
            ref JournalRecord? accepted)
        {
            var expectedSeq = 1L;
            for (var i = 1; i < lines.Count; i++)
            {
                var last = i == lines.Count - 1;
                var seq = expectedSeq++;
                var read = lines[i] is { } line
                    ? JournalCodec.ReadRecord(line, seq)
                    : new JournalRecordRead(
                        JournalLineStatus.Corrupt, null,
                        new Diagnostic(
                            "", Severity.Error,
                            "A journal record line is not valid UTF-8.", JsonPath: "$"));
                // Every envelope the codec could read spends its seq position
                // (SPEC §F2) — a Skipped or refused one included — so LastSeq
                // is the last valid envelope seq and the next append at
                // LastSeq + 1 never reuses a spent number.
                if (read.Status != JournalLineStatus.Corrupt)
                {
                    fold.ObserveSeq(seq);
                }
                if (read.Status == JournalLineStatus.Decoded)
                {
                    var record = read.Record!;
                    if (accepted is null && record.Type == JournalRecordType.RunAccepted)
                    {
                        accepted = record;
                    }
                    if (!fold.Apply(record))
                    {
                        review = true;
                        reviewReason = "A journal record could not be interpreted.";
                        return;
                    }
                }
                else if (read.Status == JournalLineStatus.Skipped)
                {
                    continue;
                }
                else if (read.Status == JournalLineStatus.Corrupt && last &&
                         IsDroppableLastLine(lines[i]))
                {
                    // SPEC §F3 item 3 (with §F2 item 2): the droppable last
                    // line is only the tail of an interrupted write — a line
                    // that cannot be read back at all: undecodable UTF-8,
                    // unparseable JSON or a damaged record envelope (a
                    // missing or non-boolean `required` member included).
                    // A complete, readable envelope — a seq gap or
                    // duplicate, a crc mismatch — is corruption at a real
                    // position and falls through to NeedsReview.
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_STORE_002,
                        "The journal's last line is corrupt and was dropped.",
                        severity: Severity.Warning));
                    return;
                }
                else
                {
                    review = true;
                    reviewReason = read.Diagnostic!.Message;
                    return;
                }
            }
        }

        // The SPEC §F3 item 3 droppable tail, shared by the load fold and the
        // append path's boundary check so a tail is truncated only when Load
        // would drop it anyway. The "broken" line of §F3 is a line that
        // cannot be read back as a record envelope at all: undecodable UTF-8
        // (a null line), unparseable JSON or a damaged member — the missing
        // or non-boolean `required` of SPEC §F2 item 2 and any other damaged
        // required envelope member included. A complete envelope that fails
        // integrity — a seq gap or duplicate, a crc mismatch, a
        // checksum-valid record whose `required` disagrees with the §F2
        // table — is a well-formed record, not a torn write: it stays
        // NeedsReview and is never dropped (§F3 item 3 drops only the broken
        // line; the codec's Corrupt class covers both shapes, so the crc or
        // seq failure of a readable record is corruption at a real
        // position — the same outcome §F2 item 2 gives a middle line).
        private static bool IsDroppableLastLine(string? line) =>
            line is null || JournalCodec.IsBrokenLine(line);

        // The RunAccepted data.planFileDigest string, or null when absent or
        // mistyped (SPEC §F2: required member).
        private static string? PlanFileDigestOf(JournalRecord accepted)
        {
            var data = accepted.Data.ToToken();
            return data[PlanFileDigestMember] is JValue value &&
                value.Type == JTokenType.String && value.Value is string digest &&
                digest.Length != 0
                ? digest
                : null;
        }

        // Rewrites the derived caches so they reflect the journal fold: a
        // missing, unreadable or stale state.json is regenerated and the
        // index entry is refreshed (SPEC §F1, §F3 item 2). Both are caches —
        // a write failure never fails the load.
        private void RefreshCaches(string runId, string? workflowId, RunState state)
        {
            try
            {
                var path = Path.Combine(RunDirectory(runId), StateFileName);
                var stale = true;
                if (ReadCacheJson(path) is JObject existing &&
                    JournalFold.TryParseRunState(
                        StringField(existing, StateMember), out var recorded) &&
                    recorded == state)
                {
                    stale = false;
                }
                if (stale)
                {
                    ReplaceAtomically(
                        path, StateJsonBytes(runId, state, UtcTimestamp.Now()));
                }
            }
            catch (Exception e) when (IsIoFailure(e))
            {
            }
            try
            {
                UpdateIndex(runId, workflowId, state, UtcTimestamp.Now());
            }
            catch (Exception e) when (IsIoFailure(e))
            {
            }
        }

        // Reads one of the regenerable caches (state.json, index.json) as
        // parsed JSON, or null when the file is missing or unreadable — an
        // I/O failure, bytes that are not valid UTF-8 and content that does
        // not parse are all "unreadable cache", never an error: the journal
        // is the single truth and the caller rebuilds the cache from it
        // (SPEC §F1). No exception may escape a cache read path.
        private static JToken? ReadCacheJson(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                var parsed = CanonicalJson.Parse(File.ReadAllText(path, s_utf8));
                return parsed.Ok ? parsed.Value : null;
            }
            catch (Exception e) when (IsIoFailure(e) || e is DecoderFallbackException)
            {
                return null;
            }
        }

        // index.json parsed into entries, or null when missing or not a
        // readable index document — the caller rebuilds from the journals.
        private List<RunIndexEntry>? ReadIndexEntries()
        {
            var path = Path.Combine(_root, IndexFileName);
            if (ReadCacheJson(path) is not JObject obj ||
                obj[RunsMember] is not JArray runs)
            {
                return null;
            }
            var entries = new List<RunIndexEntry>(runs.Count);
            foreach (var item in runs)
            {
                if (!TryReadIndexEntry(item, out var entry))
                {
                    return null;
                }
                entries.Add(entry!);
            }
            return entries;
        }

        // One index.json entry decoded through safe field parsing only —
        // the same checks every cache reader applies, so a malformed entry
        // (a missing or non-string runId, a non-string state, a missing
        // timestamp) never throws on a JToken conversion; it marks the
        // whole cache corrupt and the caller rebuilds from the journals.
        private static bool TryReadIndexEntry(JToken item, out RunIndexEntry? entry)
        {
            entry = null;
            if (item is not JObject obj)
            {
                return false;
            }
            var runId = StringField(obj, RunIdMember);
            var workflowId = StringField(obj, WorkflowIdMember);
            var createdAt = StringField(obj, CreatedAtMember);
            var updatedAt = StringField(obj, UpdatedAtMember);
            if (runId is null || workflowId is null || createdAt is null ||
                updatedAt is null ||
                !JournalFold.TryParseRunState(
                    StringField(obj, StateMember), out var state))
            {
                return false;
            }
            entry = new RunIndexEntry(runId, workflowId, state, createdAt, updatedAt);
            return true;
        }

        // The Runs the directories prove exist: every run_<ULID> directory
        // under Runs/. SPEC §F1/§F3 make the journal the single truth, so
        // this set — not the index.json cache — decides which Runs a
        // rebuild publishes and List may return. A directory without a
        // journal is still included: Load classifies the missing journal
        // NeedsReview, and a damaged Run must surface in List and the
        // rebuilt index rather than silently disappear as an "incomplete
        // creation". The scan fails closed and never confuses absence with
        // failure: a genuinely absent Runs directory — or one that raced
        // away mid-scan — is absence and yields the empty set, while an
        // inaccessible Runs path, a non-directory occupying it, a journal
        // whose metadata cannot be inspected and every other enumeration
        // failure are OW-STORE-001. Neither
        // Directory.Exists/File.Exists nor FileInfo.Exists is usable
        // here — they answer false on I/O and access failures and would
        // read an inaccessible store as an empty one.
        private Result<List<string>> PersistedRunIds()
        {
            var runIds = new List<string>();
            var runsDir = Path.Combine(_root, RunsDirectoryName);
            FileAttributes attributes;
            try
            {
                attributes = InspectAttributes(runsDir);
            }
            catch (Exception e) when (
                e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                return Result<List<string>>.Success(runIds);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Result<List<string>>.Failure(StoreDiagnostic(
                    "The Run Store Runs directory could not be inspected."));
            }
            // File.GetAttributes reports a missing path as
            // (FileAttributes)(-1) on the Unix metadata path instead of
            // throwing FileNotFoundException.
            if (attributes == (FileAttributes)(-1))
            {
                return Result<List<string>>.Success(runIds);
            }
            if ((attributes & FileAttributes.Directory) == 0)
            {
                return Result<List<string>>.Failure(StoreDiagnostic(
                    "The Run Store Runs path is occupied by a non-directory."));
            }
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(runsDir);
            }
            catch (Exception e) when (
                e is FileNotFoundException || e is DirectoryNotFoundException)
            {
                // The directory raced away between the metadata check and
                // the enumeration: still absence, not failure.
                return Result<List<string>>.Success(runIds);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return Result<List<string>>.Failure(StoreDiagnostic(
                    "The Run Store Runs directory could not be enumerated."));
            }
            foreach (var dir in dirs)
            {
                var runId = Path.GetFileName(dir);
                if (!IsRunId(runId))
                {
                    continue;
                }
                try
                {
                    // The journal's presence does not decide membership —
                    // a journal-less Run directory is a damaged Run List
                    // surfaces as NeedsReview — but a metadata inspection
                    // that itself fails still fails the scan closed.
                    InspectAttributes(Path.Combine(dir, JournalFileName));
                }
                catch (Exception e) when (
                    e is FileNotFoundException || e is DirectoryNotFoundException)
                {
                }
                catch (Exception e) when (IsIoFailure(e))
                {
                    return Result<List<string>>.Failure(StoreDiagnostic(
                        "The journal file of a Run could not be inspected."));
                }
                runIds.Add(runId);
            }
            return Result<List<string>>.Success(runIds);
        }

        // Rebuilds index.json from the per-Run journals — the single truth —
        // and writes the regenerated index back through the atomic replace
        // when the cache differs from it. This is also UpdateIndex's only
        // way to publish an index that is missing or unreadable: the scan's
        // per-Run Load refreshes the caches, which reaches UpdateIndex
        // while the file is still absent, so a nested call returns empty —
        // the rebuild already in progress writes that Run's entry. A scan
        // that cannot even establish the persisted set publishes nothing —
        // better a missing or stale index.json, which List never trusts
        // for its results, than a partial membership.
        private List<RunIndexEntry> RebuildIndex()
        {
            if (_indexRebuildInProgress)
            {
                return new List<RunIndexEntry>();
            }
            var persisted = PersistedRunIds();
            return persisted.Ok
                ? RebuildIndex(persisted.Value!)
                : new List<RunIndexEntry>();
        }

        private List<RunIndexEntry> RebuildIndex(List<string> persistedIds)
        {
            if (_indexRebuildInProgress)
            {
                return new List<RunIndexEntry>();
            }
            var entries = DeriveIndexEntries(persistedIds);
            WriteIndexCache(entries);
            return entries;
        }

        // Folds every persisted Run through Load — the journal-side truth —
        // into its index entry, so membership and state always come from
        // the journals (a missing or corrupt journal is the NeedsReview
        // Load itself produces, never a dropped Run). The reentrancy guard
        // covers the whole scan: every Load refreshes the caches, which
        // reaches UpdateIndex while the index file may still be missing or
        // unreadable; the guard makes that nested rebuild a no-op so a
        // partial membership never preempts the complete derived set.
        private List<RunIndexEntry> DeriveIndexEntries(List<string> persistedIds)
        {
            var entries = new List<RunIndexEntry>();
            if (_indexRebuildInProgress)
            {
                return entries;
            }
            _indexRebuildInProgress = true;
            try
            {
                foreach (var runId in persistedIds)
                {
                    var load = Load(runId);
                    if (!load.Ok)
                    {
                        continue;
                    }
                    var snapshot = load.Value!;
                    entries.Add(new RunIndexEntry(
                        runId,
                        snapshot.Plan?.WorkflowId ?? "",
                        snapshot.State,
                        snapshot.CreatedAt ?? "",
                        snapshot.UpdatedAt ?? ""));
                }
            }
            finally
            {
                _indexRebuildInProgress = false;
            }
            return entries;
        }

        // Writes the journal-derived entries to index.json through the
        // atomic replace — but only when the cache does not already hold
        // exactly this content, so a List over an unchanged store never
        // pays for a redundant replace. Best-effort cache maintenance: a
        // write failure leaves the next List or Load to regenerate.
        private void WriteIndexCache(List<RunIndexEntry> entries)
        {
            if (IndexEntriesEqual(ReadIndexEntries(), entries))
            {
                return;
            }
            try
            {
                var root = new JObject();
                var runs = new JArray();
                foreach (var entry in entries)
                {
                    runs.Add(new JObject
                    {
                        [RunIdMember] = entry.RunId,
                        [WorkflowIdMember] = entry.WorkflowId,
                        [StateMember] = JsonNaming.ToSnakeCase(entry.State.ToString()),
                        [CreatedAtMember] = entry.CreatedAt,
                        [UpdatedAtMember] = entry.UpdatedAt,
                    });
                }
                root[RunsMember] = runs;
                ReplaceAtomically(
                    Path.Combine(_root, IndexFileName),
                    s_utf8.GetBytes(CanonicalJson.Serialize(root)));
            }
            catch (Exception e) when (IsIoFailure(e))
            {
            }
        }

        // Entry-for-entry equality between the decoded cache entries and
        // the journal-derived set: a missing or corrupt cache (null), a
        // different membership or a differing field is a difference.
        private static bool IndexEntriesEqual(
            List<RunIndexEntry>? cached, List<RunIndexEntry> derived)
        {
            if (cached is null || cached.Count != derived.Count)
            {
                return false;
            }
            for (var i = 0; i < cached.Count; i++)
            {
                var a = cached[i];
                var b = derived[i];
                if (a.State != b.State ||
                    !string.Equals(a.RunId, b.RunId, StringComparison.Ordinal) ||
                    !string.Equals(a.WorkflowId, b.WorkflowId, StringComparison.Ordinal) ||
                    !string.Equals(a.CreatedAt, b.CreatedAt, StringComparison.Ordinal) ||
                    !string.Equals(a.UpdatedAt, b.UpdatedAt, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        // The fold behind the state.json/index.json cache maintenance: the
        // store instance's remembered fold when it has one, otherwise the
        // journal folded from disk (best effort — only decoded records, only
        // while the header is a known v1 header).
        private JournalFold FoldFor(string runId)
        {
            if (_fold.TryGetValue(runId, out var fold))
            {
                return fold;
            }
            fold = JournalFold.Empty();
            var lines = ReadJournalLines(JournalPath(runId));
            if (lines is { Count: > 0 } && lines[0] is { } first &&
                JournalCodec.ReadHeader(first).Status == JournalLineStatus.Decoded)
            {
                var expectedSeq = 1L;
                for (var i = 1; i < lines.Count; i++)
                {
                    var seq = expectedSeq++;
                    var read = lines[i] is { } line
                        ? JournalCodec.ReadRecord(line, seq)
                        : new JournalRecordRead(
                            JournalLineStatus.Corrupt, null, null);
                    if (read.Status != JournalLineStatus.Corrupt)
                    {
                        fold.ObserveSeq(seq);
                    }
                    if (read.Status == JournalLineStatus.Skipped)
                    {
                        continue;
                    }
                    if (read.Status != JournalLineStatus.Decoded ||
                        !fold.Apply(read.Record!))
                    {
                        break;
                    }
                }
            }
            _fold[runId] = fold;
            return fold;
        }

        private static string? StringField(JObject? obj, string name) =>
            obj?[name] is JValue value && value.Type == JTokenType.String &&
                value.Value is string text
                ? text
                : null;

        // Internal test seam ahead of the state-update member of T-W1.7-03:
        // rewrites the regenerable state.json cache of an existing Run
        // through the same fsync'd temp-file/atomic-replace path, so tests
        // exercise an atomic replace over an existing target.
        internal Result<Unit> RewriteStateCache(string runId, RunState state)
        {
            RequireRunId(runId);
            try
            {
                var path = Path.Combine(RunDirectory(runId), StateFileName);
                if (!File.Exists(path))
                {
                    return StoreFailure("The state file of the Run does not exist.");
                }
                ReplaceAtomically(path, StateJsonBytes(runId, state, UtcTimestamp.Now()));
                return Result<Unit>.Success(Unit.Value);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return StoreFailure("The Run state could not be persisted.");
            }
        }

        private Result<Unit> CreateRun(
            string runId, CompiledPlan plan, WorkflowDocument doc, ActorKind? actor)
        {
            RequireRunId(runId);
            if (plan is null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            if (doc is null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            // Codec contract violations are programming errors and propagate;
            // only the file writes below are mapped to OW-STORE-001.
            var planBytes = s_utf8.GetBytes(PlanJsonCodec.Write(plan));
            var planFileDigest = Sha256LowerHex(planBytes);
            var workflowBytes = s_utf8.GetBytes(WorkflowSerializer.Write(doc));
            var now = UtcTimestamp.Now();
            var state = plan.Approvals.Count == 0
                ? RunState.Queued
                : RunState.AwaitingApproval;
            var runDir = RunDirectory(runId);
            try
            {
                if (File.Exists(runDir) || Directory.Exists(runDir))
                {
                    return StoreFailure("The Run directory already exists.");
                }
                // SPEC §0.6 SG-16: a tombstoned runId is never reused —
                // a Run accepted under it would resurrect a deleted
                // identity. The check precedes the create and is
                // repeated after the directory lands, closing the
                // window in which another Writer's tombstone commit
                // could interleave with this create; the residue sweep
                // stays the backstop for the remaining cross-process
                // race.
                var tombstones = ReadTombstoneIds();
                if (!tombstones.Ok)
                {
                    return Result<Unit>.Failure(tombstones.Diagnostics);
                }
                if (tombstones.Value!.Contains(runId))
                {
                    return Result<Unit>.Failure(DeletedRunDiagnostic());
                }
                Directory.CreateDirectory(runDir);
                WriteNewFile(Path.Combine(runDir, PlanFileName), planBytes, flushToDisk: true);
                WriteNewFile(
                    Path.Combine(runDir, WorkflowFileName), workflowBytes, flushToDisk: true);
                WriteNewFile(
                    Path.Combine(runDir, JournalFileName),
                    JournalBytes(runId, plan, actor, planFileDigest, now),
                    flushToDisk: true);
                ReplaceAtomically(
                    Path.Combine(runDir, StateFileName), StateJsonBytes(runId, state, now));
                // The SPEC §F7 slot: finalized at Run end, created empty.
                ReplaceAtomically(
                    Path.Combine(runDir, ChangesFileName), s_utf8.GetBytes("[]"));
                UpdateIndex(runId, plan.WorkflowId, state, now);
                _fold[runId] = JournalFold.ForNewRun(plan.Approvals.Count);
                var recheck = ReadTombstoneIds();
                if (!recheck.Ok)
                {
                    return Result<Unit>.Failure(recheck.Diagnostics);
                }
                if (recheck.Value!.Contains(runId))
                {
                    return Result<Unit>.Failure(DeletedRunDiagnostic());
                }
                return Result<Unit>.Success(Unit.Value);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                return StoreFailure("The Run could not be persisted.");
            }
        }

        // plan.json/workflow.json are immutable: CreateNew refuses to
        // overwrite, so a second create for the same Run cannot silently
        // replace persisted bytes.
        private static void WriteNewFile(string path, byte[] bytes, bool flushToDisk)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk);
            }
        }

        // Internal test seam for the transient-retry path of ReplaceAtomically:
        // invoked before every move/replace attempt with (temp path, target
        // path, target exists); a non-null return is thrown instead of the
        // real step, so tests inject a transient failure deterministically.
        // Production callers leave it null.
        internal Func<string, string, bool, Exception?>? ReplaceStepProbe { get; set; }

        // Internal test seam for the journal append of SPEC §F3 item 1: when
        // set it wraps the freshly opened journal stream, and the returned
        // stream is the one WriteJournalLines writes and fsyncs, so tests
        // observe the ordered Write → Flush(flushToDisk) sequence and its
        // argument without replacing it — the production path always makes
        // both calls itself; a conforming wrapper forwards them to the real
        // stream. Production callers leave it null.
        internal Func<FileStream, FileStream>? JournalStreamWrapper { get; set; }

        // Internal test seam for the SG-16 tombstone writes, same shape as
        // JournalStreamWrapper: the returned stream is the one the
        // tombstone append and EnsureDurable write and fsync, so tests
        // observe that each tombstone Flush(flushToDisk) happens while
        // the Run directory still exists — the fsync-before-delete
        // ordering of SPEC §0.6 SG-16 — without replacing the write.
        // Production callers leave it null.
        internal Func<FileStream, FileStream>? TombstoneStreamWrapper { get; set; }

        // Internal test seam for the SG-16 tombstone's durable publish —
        // the DurableRename.Move step SPEC §0.6 requires be durable
        // before the Run directory and index entry are removed. When set
        // it replaces the real durable rename in DeletedRuns.Append and
        // DeletedRuns.EnsureDurable with (temp path, committed path), so
        // tests record the durable publish's ordering against the
        // destructive half and inject a publish failure
        // deterministically; a conforming seam forwards to
        // DurableRename.Move. Production callers leave it null.
        internal Action<string, string>? TombstonePublishStep { get; set; }

        // Internal test seam for the persisted-Run scan behind List and
        // the index rebuild: invoked before every metadata probe of the
        // scan (the Runs directory and each Run's journal) with the path
        // about to be inspected; a non-null return is thrown instead of
        // the real step, so tests inject an enumeration or metadata
        // failure deterministically. Production callers leave it null.
        internal Func<string, Exception?>? PersistedScanProbe { get; set; }

        // One metadata probe of the persisted-Run scan, routed through
        // the test seam. File.GetAttributes is the inspection — never
        // File.Exists/Directory.Exists, which answer false on I/O and
        // access failures and would read an inaccessible store as an
        // empty one: absence surfaces as
        // FileNotFoundException/DirectoryNotFoundException (or the
        // (FileAttributes)(-1) answer of the Unix metadata path) and is
        // told apart from a real failure by the caller.
        private FileAttributes InspectAttributes(string path)
        {
            var simulated = PersistedScanProbe?.Invoke(path);
            if (simulated is not null)
            {
                throw simulated;
            }
            return File.GetAttributes(path);
        }

        // SPEC §F3 item 2: a temp file in the same directory is fsync'd, then
        // atomically moved over the target, so a crash leaves the old or the
        // new content — never a torn file. The replace step itself is retried
        // a bounded number of times on the transient Win32 sharing failures an
        // external reader (indexer, virus scanner) causes while it briefly
        // holds the target or the fresh temp file — the T-W1.7-02 transient
        // OW-STORE-001 root cause, reproduced as a bare File.Replace failure
        // with ERROR_UNABLE_TO_REMOVE_REPLACED.
        private void ReplaceAtomically(string path, byte[] bytes)
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
                    MoveOrReplace(temp, path);
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

        private void MoveOrReplace(string temp, string path)
        {
            var targetExists = File.Exists(path);
            var simulated = ReplaceStepProbe?.Invoke(temp, path, targetExists);
            if (simulated is not null)
            {
                throw simulated;
            }
            if (targetExists)
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        // The transient Win32 codes the replace step surfaces while an
        // external reader holds a file: ERROR_ACCESS_DENIED (5),
        // ERROR_SHARING_VIOLATION (32) and ERROR_UNABLE_TO_REMOVE_REPLACED
        // (1175 — the code the reproduction actually produced).
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

        // The header line plus the Flushed RunAccepted record whose data
        // carries the SHA-256 of the exact plan.json bytes (SPEC §F2, §D7
        // item 6). Every line is terminated with '\n' (SPEC §F2).
        private byte[] JournalBytes(
            string runId, CompiledPlan plan, ActorKind? actor, string planFileDigest, string now)
        {
            var data = new JObject();
            if (actor is { } kind)
            {
                data[ActorMember] = JsonNaming.ToSnakeCase(kind.ToString());
            }
            var approvals = new JArray();
            foreach (var requirement in plan.Approvals)
            {
                approvals.Add(new JObject
                {
                    [NodeIdMember] = requirement.NodeId,
                    [EffectMember] = requirement.Effect,
                    [DetailMember] = requirement.Detail,
                });
            }
            data[ApprovalsMember] = approvals;
            data[PlanFileDigestMember] = planFileDigest;
            var accepted = new JournalRecord(
                Seq: 1, Ts: now, Session: _session, Type: JournalRecordType.RunAccepted,
                Step: null, Attempt: null, Data: JsonValue.From(data));
            var text = JournalCodec.WriteHeader(new JournalHeader(runId, plan.PlanHash)) +
                "\n" + JournalCodec.WriteRecord(accepted) + "\n";
            return s_utf8.GetBytes(text);
        }

        private byte[] StateJsonBytes(string runId, RunState state, string now) =>
            s_utf8.GetBytes(CanonicalJson.Serialize(new JObject
            {
                [RunIdMember] = runId,
                [StateMember] = JsonNaming.ToSnakeCase(state.ToString()),
                [UpdatedAtMember] = now,
            }));

        // index.json = {"runs":[{runId,workflowId,state,createdAt,updatedAt}]}
        // (SPEC §F1). A readable index with valid entries is preserved
        // entry-for-entry; a missing, unreadable or corrupt one is never
        // completed from this Run alone — List would accept the partial
        // document and hide the Runs recovery must discover — so the same
        // journal-side rebuild List uses writes the complete index, this
        // Run's entry included.
        private void UpdateIndex(string runId, string? workflowId, RunState state, string now)
        {
            var path = Path.Combine(_root, IndexFileName);
            JObject? root = null;
            JArray? runs = null;
            if (ReadCacheJson(path) is JObject existing &&
                existing[RunsMember] is JArray existingRuns)
            {
                root = existing;
                runs = existingRuns;
            }
            if (root is null || runs is null)
            {
                // A call nested inside an in-progress rebuild lands here
                // while the file is still absent: RebuildIndex returns
                // empty and the rebuild writes this Run's entry itself.
                RebuildIndex();
                return;
            }
            // A syntactically valid document whose entries are not all valid
            // index entries is a corrupt cache, not a partial one: the same
            // safe field parsing ReadIndexEntries applies decides, and any
            // malformed entry — a non-string runId included — sends the
            // whole index to the journal-side rebuild. No JToken conversion
            // may run on document content here.
            JObject? previous = null;
            foreach (var item in runs)
            {
                if (!TryReadIndexEntry(item, out var parsed))
                {
                    RebuildIndex();
                    return;
                }
                if (previous is null &&
                    string.Equals(parsed!.RunId, runId, StringComparison.Ordinal))
                {
                    previous = (JObject)item;
                }
            }
            // A refreshed entry keeps its first-seen createdAt and, when the
            // caller does not know it (a plan that could not be decoded), the
            // workflowId already recorded.
            var entry = new JObject
            {
                [RunIdMember] = runId,
                [WorkflowIdMember] = workflowId ?? StringField(previous, WorkflowIdMember) ?? "",
                [StateMember] = JsonNaming.ToSnakeCase(state.ToString()),
                [CreatedAtMember] = StringField(previous, CreatedAtMember) ?? now,
                [UpdatedAtMember] = now,
            };
            if (previous is null)
            {
                runs.Add(entry);
            }
            else
            {
                runs[runs.IndexOf(previous)] = entry;
            }
            ReplaceAtomically(path, s_utf8.GetBytes(CanonicalJson.Serialize(root)));
        }

        // FileMode.Append keeps every prior line and FileShare.Read leaves
        // the journal readable while the store holds it; the optional test
        // seam wraps the stream before WriteJournalLines uses it.
        private FileStream OpenJournalStream(string journalPath)
        {
            var stream = new FileStream(
                journalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            try
            {
                return JournalStreamWrapper is { } wrap ? wrap(stream) : stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // Pending Buffered lines go first (they precede the new record in the
        // journal's sequence), then the new line; the stream is fsync'd before
        // the call returns (SPEC §F3 item 1). A torn tail Load would drop is
        // discarded to a record boundary before the write (SPEC §F3 item 3).
        private void WriteJournalLines(string journalPath, string runId, string? extraLine)
        {
            List<string>? pending = null;
            if (_pending.TryGetValue(runId, out var lines) && lines.Count != 0)
            {
                pending = lines;
            }
            var builder = new StringBuilder();
            EnsureJournalBoundary(journalPath, builder);
            if (pending is not null)
            {
                foreach (var line in pending)
                {
                    builder.Append(line).Append('\n');
                }
            }
            if (extraLine is not null)
            {
                builder.Append(extraLine).Append('\n');
            }
            var bytes = s_utf8.GetBytes(builder.ToString());
            using (var stream = OpenJournalStream(journalPath))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (pending is not null)
            {
                pending.Clear();
                _pending.Remove(runId);
            }
        }

        // Before any line is written the journal must end at a record
        // boundary (SPEC §F3): a final record the load path itself would
        // drop must never precede the new line — the record Load drops
        // becomes middle corruption on the next read once another line
        // lands after it. The final record is therefore classified with
        // the same IsDroppableLastLine (§F3 item 3) whether or not it
        // ends with '\n': torn bytes without a terminator and a
        // newline-terminated line whose envelope is broken — a missing or
        // non-boolean `required` included — are the same droppable tail
        // and are truncated, terminator included, to just after the last
        // complete line and fsync'd. A final record Load would keep — a
        // complete record that lost only its terminator, or a complete
        // envelope failing integrity — is never truncated: an
        // unterminated one is separated from the new line by the missing
        // '\n' instead, and the next load's NeedsReview still sees the
        // corruption. A file without any '\n' holds only a partial
        // header, which is not a record line. Every failure is an I/O
        // failure for the caller to map to OW-STORE-001.
        private static void EnsureJournalBoundary(string journalPath, StringBuilder builder)
        {
            var raw = File.ReadAllBytes(journalPath);
            if (raw.Length == 0)
            {
                return;
            }
            // The final record is the segment after the previous '\n',
            // ending at the file's last '\n' when the tail is terminated
            // or at EOF when a torn write left it unterminated. It is a
            // record line only when a '\n' precedes it: a file without a
            // second '\n' holds only the header (or a partial header),
            // which the record fold never sees.
            var terminated = raw[raw.Length - 1] == (byte)'\n';
            var recordEnd = terminated ? raw.Length - 1 : raw.Length;
            var recordStart = 0;
            for (var i = recordEnd - 1; i >= 0; i--)
            {
                if (raw[i] == (byte)'\n')
                {
                    recordStart = i + 1;
                    break;
                }
            }
            if (recordStart == 0)
            {
                if (!terminated)
                {
                    builder.Append('\n');
                }
                return;
            }
            if (IsDroppableLastLine(
                    DecodeLine(raw, recordStart, recordEnd - recordStart)))
            {
                using (var stream = new FileStream(
                           journalPath, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    stream.SetLength(recordStart);
                    stream.Flush(flushToDisk: true);
                }
            }
            else if (!terminated)
            {
                builder.Append('\n');
            }
        }

        private List<string> PendingLines(string runId)
        {
            if (!_pending.TryGetValue(runId, out var lines))
            {
                lines = new List<string>();
                _pending.Add(runId, lines);
            }
            return lines;
        }

        private string RunDirectory(string runId) =>
            Path.Combine(_root, RunsDirectoryName, runId);

        private string JournalPath(string runId) =>
            Path.Combine(RunDirectory(runId), JournalFileName);

        // runId is "run_" + ULID (SPEC §0.4); the strict grammar also keeps
        // path separators and traversal out of the Run directory name.
        private static void RequireRunId(string runId)
        {
            if (runId is null)
            {
                throw new ArgumentNullException(nameof(runId));
            }
            if (!IsRunId(runId))
            {
                throw new ArgumentException(
                    "runId must be a \"run_\" + ULID identifier.", nameof(runId));
            }
        }

        private static bool IsRunId(string? runId)
        {
            if (runId is null ||
                runId.Length != Identifiers.RunIdPrefix.Length + UlidLength ||
                !runId.StartsWith(Identifiers.RunIdPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            for (var i = Identifiers.RunIdPrefix.Length; i < runId.Length; i++)
            {
                if (UlidAlphabet.IndexOf(runId[i]) < 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static string Sha256LowerHex(byte[] bytes)
        {
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }
            const string digits = "0123456789abcdef";
            var chars = new char[hash.Length * 2];
            for (var i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = digits[hash[i] >> 4];
                chars[i * 2 + 1] = digits[hash[i] & 0xF];
            }
            return new string(chars);
        }

        private static bool IsIoFailure(Exception e) =>
            e is IOException || e is UnauthorizedAccessException ||
            e is NotSupportedException || e is System.Security.SecurityException;

        private static Diagnostic StoreDiagnostic(string message) =>
            ErrorCodes.Create(ErrorCodes.OW_STORE_001, message);

        private static Result<Unit> StoreFailure(string message) =>
            Result<Unit>.Failure(StoreDiagnostic(message));
    }
}
