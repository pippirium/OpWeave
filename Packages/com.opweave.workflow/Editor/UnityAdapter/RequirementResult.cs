#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The outcome of evaluating one or more SPEC §G1 item 4 Requirements —
    /// a <see cref="RequirementVerdict"/> plus the failure
    /// <see cref="Core.Diagnostic"/> when the verdict is
    /// <see cref="RequirementVerdict.Violated"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="RequirementVerdict.Wait"/> and
    /// <see cref="RequirementVerdict.Satisfied"/> carry no diagnostic: a Wait
    /// is "not satisfied yet" rather than an error, so no error code is ever
    /// attached to it (the 2026-11-06 <c>not_compiling</c> decision, SPEC
    /// §1.4, §E4 step 2 and §G1 item 4).
    /// </remarks>
    public readonly struct RequirementResult
    {
        private RequirementResult(RequirementVerdict verdict, Diagnostic? diagnostic)
        {
            Verdict = verdict;
            Diagnostic = diagnostic;
        }

        /// <summary>The evaluation verdict.</summary>
        public RequirementVerdict Verdict { get; }

        /// <summary>
        /// The failure diagnostic — non-null only when
        /// <see cref="Verdict"/> is <see cref="RequirementVerdict.Violated"/>.
        /// </summary>
        public Diagnostic? Diagnostic { get; }

        /// <summary>A satisfied requirement.</summary>
        public static RequirementResult Satisfied { get; } =
            new RequirementResult(RequirementVerdict.Satisfied, null);

        /// <summary>
        /// A requirement that is not satisfied now but resolves by waiting —
        /// no error code, no diagnostic.
        /// </summary>
        public static RequirementResult Wait { get; } =
            new RequirementResult(RequirementVerdict.Wait, null);

        /// <summary>A violated requirement reported by <paramref name="diagnostic"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is null.</exception>
        public static RequirementResult Violated(Diagnostic diagnostic) =>
            new RequirementResult(
                RequirementVerdict.Violated,
                diagnostic ?? throw new ArgumentNullException(nameof(diagnostic)));
    }
}
