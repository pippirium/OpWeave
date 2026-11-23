#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The exception a <see cref="StepContext"/> implementation raises when a
    /// value conversion fails with a real diagnostic — a Ref that cannot be
    /// resolved (SPEC §E5: OW-UNITY-001) or a value its Codec cannot convert
    /// (SPEC §B7.1: OW-RUN-007 at run time). The step executor unwraps it and
    /// fails the step with <see cref="Diagnostics"/> instead of the generic
    /// OW-RUN-006 an arbitrary Operation exception earns (SPEC §0.6 SG-06:
    /// "Get/SetOutput の型不一致は適切な型/出力診断").
    /// </summary>
    /// <remarks>
    /// This is deliberately <em>not</em> how a missing or pending port read
    /// reports itself: those are programming errors (<see cref="InvalidOperationException"/>),
    /// which SPEC §C2 item 8 maps to OW-RUN-006 like every other Operation
    /// exception. Core-internal so only the sanctioned context
    /// implementations can raise it.
    /// </remarks>
    internal sealed class StepContextException : Exception
    {
        /// <summary>
        /// Carries the diagnostics the step fails with; the list must be
        /// non-empty because the step failure record journals the first entry.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="diagnostics"/> is empty.</exception>
        internal StepContextException(IReadOnlyList<Diagnostic> diagnostics)
            : base(diagnostics is { Count: > 0 } ? diagnostics[0].Message : string.Empty)
        {
            if (diagnostics is null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }
            if (diagnostics.Count == 0)
            {
                throw new ArgumentException(
                    "A step context failure must carry at least one diagnostic.",
                    nameof(diagnostics));
            }
            Diagnostics = ReadOnlyCollections.CopyList(diagnostics);
        }

        /// <summary>The same convenience as the list form for a single diagnostic.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is null.</exception>
        internal StepContextException(Diagnostic diagnostic)
            : this(new[] { diagnostic ?? throw new ArgumentNullException(nameof(diagnostic)) })
        {
        }

        /// <summary>The diagnostics the step executor reports as the step failure.</summary>
        internal IReadOnlyList<Diagnostic> Diagnostics { get; }
    }
}
