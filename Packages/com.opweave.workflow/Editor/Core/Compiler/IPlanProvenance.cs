#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The injected source of the planHash material the compiler cannot
    /// derive itself (SPEC §D7 item 5, §0.6 SG-11): the six Unity environment
    /// identifiers and the weak per-Type/Codec fingerprint atoms of the
    /// definitions a Plan uses. UnityAdapter produces the production instance
    /// (W2.2); Core hosts the contract only (SPEC §0.5 — no Unity dependency).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A snapshot-backed <see cref="IWorkflowCompiler"/> requires a
    /// provenance instance at construction: without it the Build Plan stage
    /// cannot produce the mandated hash inputs, and failing the construction
    /// is preferable to a published diagnostic that would misname the cause —
    /// the same boundary rule as the D3 static-context factory and the D6
    /// type probe.
    /// </para>
    /// <para>
    /// A <see cref="Result{T}"/> failure is reported verbatim and produces no
    /// Plan: any unobtainable required value rejects the Plan rather than
    /// substituting an empty or guessed identifier (SPEC §0.6 SG-11).
    /// </para>
    /// </remarks>
    public interface IPlanProvenance
    {
        /// <summary>
        /// The configured <see cref="IntegrityLevel"/> the SG-11 material is
        /// produced under, carried here so the level travels with the
        /// fingerprint contract it governs (the same settings source the
        /// Unity producer reads, SPEC §H4.1). The Build Plan stage evaluates
        /// it as an explicit input before producing any Plan:
        /// <see cref="IntegrityLevel.Full"/> is answered with OW-OP-011 and no
        /// Plan — never a downgrade to weak material, regardless of what the
        /// member queries would have returned (SPEC §0.1 F-05).
        /// </summary>
        IntegrityLevel IntegrityLevel { get; }

        /// <summary>
        /// The six SG-11 environment identifiers of the current Editor, or a
        /// failure the Build Plan stage reports verbatim.
        /// </summary>
        Result<PlanEnvironment> Environment { get; }

        /// <summary>
        /// The weak fingerprint atoms of the TypeProvider that registered
        /// <paramref name="definition"/>, or a failure.
        /// </summary>
        Result<PlanTypeFingerprint> TypeFingerprintFor(TypeDefinition definition);

        /// <summary>
        /// The SG-11 Codec identity atoms of the Codec
        /// <paramref name="definition"/> carries, or a failure.
        /// </summary>
        Result<PlanCodecIdentity> CodecIdentityFor(TypeDefinition definition);
    }
}
