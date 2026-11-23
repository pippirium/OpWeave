#nullable enable

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The reconstructed last recorded state of one Plan step inside a
    /// <see cref="RunSnapshot"/> (SPEC §F2, §E1): the step index the journal
    /// records carry, the state of the most recent step record, the attempt
    /// number it ran with, the outputs a <c>StepSucceeded</c> record persisted
    /// (SPEC §E5: the ValueStore is rebuilt from these) and the
    /// <c>resumeToken</c> a <c>StepWaiting</c> record persisted.
    /// </summary>
    /// <remarks>
    /// The fold records exactly what the journal says and applies no
    /// transition rules: a step absent from the journal has no
    /// <see cref="StepSnapshot"/> (it is <see cref="StepState.Pending"/>), and
    /// the reported state is the last record's, never an inferred one.
    /// <see cref="Outputs"/> is the raw <c>outputs</c> object of the last
    /// <c>StepSucceeded</c> and <see cref="ResumeToken"/> the raw
    /// <c>resumeToken</c> of the last <c>StepWaiting</c>; both stay
    /// <see cref="JsonValue"/> so recovery polls a new Operation instance with
    /// nothing else (SPEC §F4 item 5).
    /// </remarks>
    /// <remarks>
    /// The remaining members carry what the SPEC §F4 resume branch restores:
    /// <see cref="PreflightSnapshot"/> — the <c>snapshot</c> of the step's
    /// last <c>StepStarted</c>, the evidence the §F5 Reconcile consults;
    /// <see cref="Deadline"/> and <see cref="WaitingFor"/> — the journaled
    /// UTC deadline and label of the last <c>StepWaiting</c> (the deadline
    /// is never reset by a reload, SPEC §E6 item 1); <see cref="RetryNotBefore"/>
    /// — the <c>notBefore</c> instant of a decided retry
    /// (<c>step_failed{retry}</c>, SPEC §E0/§E7), whose presence also marks
    /// the Running state as "retry pending" rather than "StepStarted only";
    /// and <see cref="FailureCode"/>/<see cref="FailureMessage"/> — the
    /// recorded error of a terminal <c>StepFailed</c>.
    /// <see cref="Changes"/> is the raw <c>changes</c> array the attempt's
    /// latest record carried — the cumulative §F7 element list of the
    /// 2026-11-08 attempt-durability decision — which a resumed
    /// <c>StepWaiting</c> restores its change recorder from before the Poll
    /// continues (SPEC §F4 item 5). A record without the optional member
    /// (a pre-decision journal or an empty list) folds to null.
    /// <see cref="SucceededSeq"/> is the <c>seq</c> of the step's last
    /// <c>StepSucceeded</c> record — the completion position the SPEC §F4
    /// item 4 Editor-restart check compares when it orders a completed
    /// <c>scene.modify</c> against the last completed <c>scene.save</c>;
    /// null while no <c>StepSucceeded</c> record was folded.
    /// <see cref="OutputsSession"/> is the <c>session</c> of that same
    /// <c>StepSucceeded</c> record — the producing session the §F4 item 4
    /// handle discard compares against the current Editor session on
    /// every rebuild, so outputs minted before a successful resume's own
    /// <c>run_resumed</c> still lose their session keys.
    /// </remarks>
    public sealed record StepSnapshot(
        int Index,
        StepState State,
        int Attempt,
        JsonValue? Outputs,
        JsonValue? ResumeToken,
        JsonValue? PreflightSnapshot = null,
        string? Deadline = null,
        string? WaitingFor = null,
        string? RetryNotBefore = null,
        string? FailureCode = null,
        string? FailureMessage = null,
        JsonValue? Changes = null,
        long? SucceededSeq = null,
        string? OutputsSession = null);
}
