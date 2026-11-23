#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// The aggregate result of evaluating every node of a workflow against one
    /// <see cref="IPermissionPolicy"/> (SPEC §D6 item 1). <see cref="Diagnostics"/>
    /// carries one OW-PERM-001 error per denied effect; a non-empty list means
    /// validation fails even though approvals were still computed for the report.
    /// </summary>
    public sealed class PolicyEvaluation
    {
        internal PolicyEvaluation(
            IReadOnlyList<PermissionDecision> decisions,
            IReadOnlyList<ApprovalRequest> approvals,
            IReadOnlyList<Diagnostic> diagnostics)
        {
            Decisions = decisions;
            Approvals = approvals;
            Diagnostics = diagnostics;
        }

        /// <summary>Per-node outcomes in input order.</summary>
        public IReadOnlyList<PermissionDecision> Decisions { get; }

        /// <summary>Every <c>ask</c> effect flattened as (node, effect) pairs in input order.</summary>
        public IReadOnlyList<ApprovalRequest> Approvals { get; }

        /// <summary>One OW-PERM-001 error per denied effect, in input order.</summary>
        public IReadOnlyList<Diagnostic> Diagnostics { get; }

        /// <summary>True when at least one effect was denied (SPEC §D6 item 1).</summary>
        public bool HasDenials => Diagnostics.Count != 0;
    }
}
