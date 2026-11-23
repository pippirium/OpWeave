#nullable enable

namespace OpWeave.Testing
{
    /// <summary>
    /// The evidence answer the SPEC §M.6 item 4 probe returns for a
    /// Waitable Reconcilable step interrupted after
    /// <see cref="OpWeave.Core.Operations.IOperation.Execute"/> returned
    /// Waiting: whether the effect the suspended step issued has landed,
    /// is provably absent, or cannot be decided from the observed effect
    /// state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe inspects the effect state independently of the
    /// Operation's Reconcile — for a package the manifest, the lockfile
    /// and the registered package list — it does not repeat the
    /// Reconcile's answer. The evidence-aware
    /// <see cref="OperationContract"/> AssertIdempotency overload calls
    /// it exactly once, right after the suspended step's Reconcile
    /// returned and before anything changes the world, and judges the
    /// verdict by this row:
    /// </para>
    /// <para>
    /// <see cref="Applied"/> admits
    /// <see cref="OpWeave.Core.Operations.ReconcileResult.Completed"/>
    /// (with the §F5 identity and declared-output restoration checks) or
    /// <see cref="OpWeave.Core.Operations.ReconcileResult.Unknown"/>;
    /// <see cref="NotApplied"/> admits
    /// <see cref="OpWeave.Core.Operations.ReconcileResult.NotApplied"/>
    /// (with the §F5 NotApplied conditions) or
    /// <see cref="OpWeave.Core.Operations.ReconcileResult.Unknown"/>;
    /// <see cref="Indeterminate"/> admits
    /// <see cref="OpWeave.Core.Operations.ReconcileResult.Unknown"/>
    /// only.
    /// </para>
    /// </remarks>
    public enum EffectEvidence
    {
        /// <summary>
        /// The issued effect is applied, with the SPEC §F5 identity
        /// evidence to prove it.
        /// </summary>
        Applied,

        /// <summary>
        /// The issued effect is provably not applied: the state matches
        /// the snapshot, no issued request remains that could still apply
        /// it later, and re-execution can neither double-create nor
        /// wrongly delete (SPEC §F5 judgement condition 2 and common
        /// rule 5).
        /// </summary>
        NotApplied,

        /// <summary>
        /// The evidence cannot decide — including an effect absent right
        /// now while an issued request remains outstanding and could
        /// still apply it.
        /// </summary>
        Indeterminate,
    }
}
