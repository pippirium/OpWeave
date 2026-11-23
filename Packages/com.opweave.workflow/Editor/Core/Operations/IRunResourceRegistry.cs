#nullable enable

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The Run-resource acquisition surface of SPEC §E6 item 4: an Operation
    /// that holds a Unity-side object whose lifetime must span polls and
    /// survive a domain reload — the Prefab editing contents of
    /// unity.prefab.open_contents is the catalog example — registers it as a
    /// Run resource so the Runner can journal its lifetime
    /// (<c>resource_acquired</c> / <c>resource_released</c>, SPEC §F2) and
    /// release whatever is still open when the Run ends, including after a
    /// reload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner-approved SG-06 <see cref="StepContext"/> surface
    /// (<c>Get</c>/<c>SetOutput</c>/<c>IsPresent</c>/<c>IsPending</c>/
    /// <c>ResumeToken</c>/<c>Changes</c>/<c>Log</c>) is frozen and SPEC
    /// defines no public acquisition member, so resource acquisition is a
    /// Core-internal capability interface a <see cref="StepContext"/>
    /// implementation provides — a standard Operation reaches it by casting
    /// its context to <see cref="IRunResourceRegistry"/>. A context that
    /// provides no registry throws
    /// <see cref="System.NotSupportedException"/> from every member.
    /// </para>
    /// </remarks>
    internal interface IRunResourceRegistry
    {
        /// <summary>
        /// Registers one held object as a Run resource: a Flushed
        /// <c>resource_acquired</c> record ({kind, data}) is journaled and
        /// the acquisition is tracked for release at Run end and after a
        /// reload (SPEC §E6 item 4, §F2). <paramref name="kind"/> names the
        /// resource kind; <paramref name="data"/> carries the identity a
        /// later release needs (a Prefab's guid, for example).
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// <paramref name="kind"/> is null or empty.
        /// </exception>
        void Acquire(string kind, JsonValue? data);

        /// <summary>
        /// Whether the <em>bound step's own</em> acquisition of
        /// <c>(kind, data)</c> is still held — the §F5 evidence an Operation
        /// consults before it declares a step's effect applied. SPEC §F5's
        /// <c>open_contents</c> row grants Completed only to "同じステップの
        /// Run リソース" (the same step's Run resource), so an identically
        /// named object another step or session holds is never this step's
        /// evidence (§F5 common rule 4), and its absence is what lets a
        /// second step of one Prefab acquire its own resource. An entry whose
        /// <c>resource_acquired</c> append the store refused still counts: the
        /// object is held, and the refused append already ends the Run Failed.
        /// </summary>
        /// <param name="kind">The resource kind to look for.</param>
        /// <param name="data">
        /// The resource identity, compared with the canonical-text equality
        /// of <see cref="JsonValue"/> — exactly the pairing
        /// <see cref="Release"/> uses.
        /// </param>
        /// <returns>
        /// <see cref="RunResourceOpenState.OpenByThisStep"/> only for the
        /// bound step's own held acquisition;
        /// <see cref="RunResourceOpenState.Undetermined"/> when a held
        /// acquisition exists but its acquiring step is not recorded — the
        /// row's "state cannot be determined", which is Unknown and never a
        /// verdict of its own; <see cref="RunResourceOpenState.NotOpen"/>
        /// otherwise.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="kind"/> is null or empty.
        /// </exception>
        RunResourceOpenState IsOpen(string kind, JsonValue? data);

        /// <summary>
        /// Whether <c>(kind, data)</c> is a pair this Run still holds,
        /// whichever of its steps acquired it: the §E6 item 4 release pairing
        /// rule, which the <see cref="Release"/> of a later step applies to an
        /// earlier step's acquisition (SPEC §G6 item 5 —
        /// <c>unity.prefab.save_contents</c> closes what
        /// <c>unity.prefab.open_contents</c> opened). Use
        /// <see cref="IsOpen"/> for a step's own §F5 evidence instead.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="kind"/> is null or empty.
        /// </exception>
        bool IsHeldByRun(string kind, JsonValue? data);

        /// <summary>
        /// Releases one resource this Run acquired — the exact inverse of
        /// <see cref="Acquire"/>: a matching open (kind, data) entry is
        /// journaled <c>resource_released</c> and forgotten. Releasing a pair
        /// that was never acquired is a programming error: an unpaired
        /// <c>resource_released</c> would make the journal inconsistent under
        /// the SPEC §F2 fold.
        /// </summary>
        /// <exception cref="System.ArgumentException">
        /// <paramref name="kind"/> is null or empty, or no open resource
        /// matches (kind, data).
        /// </exception>
        void Release(string kind, JsonValue? data);

        /// <summary>
        /// The identity of the acquisition scope this registry binds: the Run
        /// and the step its <see cref="Acquire"/> records belong to, in the
        /// single <see cref="Runtime.RunResourceScope"/> form
        /// <c>"{runId}:{step}"</c>. SPEC §E6 item 4 lets a release touch only
        /// <em>the Run's own</em> resource, and a (kind, data) pair names the
        /// object, never who opened it — so the Unity side binds the object an
        /// acquisition loads to this scope, and the release handler looks that
        /// binding up again from the Run id and the <c>step</c> of the
        /// acquisition's own record. Two Runs, two steps or two sessions of one
        /// asset therefore get two scopes that cannot be confused.
        /// </summary>
        /// <remarks>
        /// An identity only: it is never journaled and never part of a record
        /// payload, so the SPEC §F2 record format and the (kind, data) pairing
        /// rule are unchanged by it.
        /// </remarks>
        string AcquisitionScope { get; }
    }

    /// <summary>
    /// The three answers SPEC §F5 distinguishes for one step's Run-resource
    /// question — its <c>unity.prefab.open_contents</c> row: Completed needs
    /// "同じステップの Run リソースが生きていて", NotApplied needs
    /// "Run リソースが無い", and Unknown covers "Run リソースの状態を判定
    ///できない". A two-valued answer would force one of the two unsafe
    /// readings: treating a resource of unknown ownership as absent invites a
    /// re-execution that duplicates the artifact, treating it as the step's own
    /// hands another owner's object to this step.
    /// </summary>
    internal enum RunResourceOpenState
    {
        /// <summary>No held acquisition of the (kind, data) pair exists —
        /// including a pair only another step holds, which is never this
        /// step's evidence.</summary>
        NotOpen,

        /// <summary>The bound step's own acquisition of the pair is held.</summary>
        OpenByThisStep,

        /// <summary>A held acquisition of the pair exists whose acquiring step
        /// is not recorded, so it can be attributed to no step: §F5's
        /// "cannot be determined", which must end in Unknown.</summary>
        Undetermined,
    }
}
