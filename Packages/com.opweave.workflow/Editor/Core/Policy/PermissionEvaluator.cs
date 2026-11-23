#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// Applies the §H4 permission stages to an <see cref="IPermissionPolicy"/>
    /// (SPEC §D6 item 1): stage 1 is the configured per-effect level, stage 2
    /// (<c>code.template</c>) follows its own setting like any other effect, and
    /// the two mandatory floors are applied here so every policy implementation
    /// shares them — <c>code.write</c> can never be below ask (stage 3,
    /// "allow は不可"), and a destructive Operation without Undo=Full is at least
    /// ask when <see cref="IPermissionPolicy.DestructiveRequiresAsk"/> holds.
    /// </summary>
    /// <remarks>
    /// Local readings where SPEC leaves detail open: the stage-3 lower bound is
    /// applied for both actors — the §H4 stage-3 settings range forbids
    /// <c>allow</c> for <c>code.write</c> outright (a human <c>allow</c> is an
    /// invalid settings value, OW-CFG-002) and §D6 states the floor explicitly
    /// for the Agent, so flooring it for every actor can never permit what the
    /// SPEC forbids; effects outside the §C4 vocabulary are clamped to Deny
    /// regardless of the configured level; duplicate effects on one node are
    /// reported once.
    /// </remarks>
    public static class PermissionEvaluator
    {
        private const string DeniedEffectMessage =
            "A side effect of this Operation is denied by the permission policy.";

        /// <summary>
        /// The effective level for one effect of one node: the configured level
        /// raised to <see cref="PermissionLevel.Ask"/> when the §H4 floors apply,
        /// or <see cref="PermissionLevel.Deny"/> for effects outside §C4.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="policy"/> or <paramref name="effect"/> is null.</exception>
        public static PermissionLevel EffectiveLevel(
            IPermissionPolicy policy, string effect, bool destructive, bool undoIsFull)
        {
            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }
            if (effect is null)
            {
                throw new ArgumentNullException(nameof(effect));
            }
            if (!EffectNames.IsKnown(effect))
            {
                // Closed §C4 vocabulary: nothing outside it may ever run.
                return PermissionLevel.Deny;
            }
            var level = policy.LevelFor(effect);
            if (effect == EffectNames.CodeWrite && level == PermissionLevel.Allow)
            {
                // §H4 stage 3: code.write cannot be configured to allow.
                level = PermissionLevel.Ask;
            }
            if (level == PermissionLevel.Allow &&
                policy.DestructiveRequiresAsk && destructive && !undoIsFull)
            {
                // §H4 item 2: destructive without Undo=Full is at least ask.
                level = PermissionLevel.Ask;
            }
            return level;
        }

        /// <summary>Evaluates one node; see <see cref="PermissionDecision"/>.</summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        public static PermissionDecision Evaluate(IPermissionPolicy policy, PermissionTarget target)
        {
            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }
            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            if (target.Effects is null)
            {
                throw new ArgumentNullException(nameof(target), "Effects must not be null.");
            }

            List<string>? approvals = null;
            List<string>? denied = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var effect in target.Effects)
            {
                if (effect is null || !seen.Add(effect))
                {
                    continue;
                }
                switch (EffectiveLevel(policy, effect, target.Destructive, target.UndoIsFull))
                {
                    case PermissionLevel.Ask:
                        (approvals ??= new List<string>()).Add(effect);
                        break;
                    case PermissionLevel.Deny:
                        (denied ??= new List<string>()).Add(effect);
                        break;
                }
            }
            return new PermissionDecision(
                target.NodeId,
                approvals ?? (IReadOnlyList<string>)Array.Empty<string>(),
                denied ?? (IReadOnlyList<string>)Array.Empty<string>());
        }

        /// <summary>
        /// Evaluates every node of a workflow in input order, producing the Plan
        /// approval list and one OW-PERM-001 error per denied effect.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        public static PolicyEvaluation Evaluate(
            IPermissionPolicy policy, IEnumerable<PermissionTarget> targets)
        {
            if (policy is null)
            {
                throw new ArgumentNullException(nameof(policy));
            }
            if (targets is null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            var decisions = new List<PermissionDecision>();
            var approvals = new List<ApprovalRequest>();
            var diagnostics = new List<Diagnostic>();
            foreach (var target in targets)
            {
                var decision = Evaluate(policy, target);
                decisions.Add(decision);
                foreach (var effect in decision.ApprovalEffects)
                {
                    approvals.Add(new ApprovalRequest(decision.NodeId, effect));
                }
                foreach (var effect in decision.DeniedEffects)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_PERM_001,
                        DeniedEffectMessage,
                        nodeId: decision.NodeId,
                        hint: effect));
                }
            }
            return new PolicyEvaluation(decisions, approvals, diagnostics);
        }
    }
}
