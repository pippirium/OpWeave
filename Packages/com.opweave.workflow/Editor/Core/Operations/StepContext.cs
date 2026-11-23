#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The context handed to <see cref="IOperation.Preflight"/>,
    /// <see cref="IOperation.Execute"/>, <see cref="IOperation.Poll"/>,
    /// <see cref="IOperation.Reconcile"/> and <see cref="IOperation.Cancel"/>:
    /// the owner-approved Core Context surface of SPEC §0.6 SG-06, exposed to
    /// standard, custom and external Operations alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Input values go only through <see cref="Get{T}"/> and outputs only
    /// through <see cref="SetOutput{T}"/> (SPEC §C2 item 5). A missing input
    /// is never converted to <see langword="null"/> — it is distinguished
    /// through <see cref="IsPresent"/>, and a pending Dry-Run value is
    /// distinguished through <see cref="IsPending"/> (SPEC §D8 item 1). Type
    /// mismatches on <see cref="Get{T}"/>/<see cref="SetOutput{T}"/> surface
    /// as the implementation's type/output diagnostics (SPEC §0.6 SG-06).
    /// </para>
    /// <para>
    /// Implementations live in the UnityAdapter (and in test fixtures); Core
    /// never hosts a Unity-specific CLR/Undo/Console implementation
    /// (SPEC §0.6 SG-06).
    /// </para>
    /// </remarks>
    public abstract class StepContext
    {
        /// <summary>Reads one input port as <typeparamref name="T"/> (SPEC §C2 item 5).</summary>
        public abstract T Get<T>(string port);

        /// <summary>Sets one output port (SPEC §C2 item 5).</summary>
        public abstract void SetOutput<T>(string port, T value);

        /// <summary>
        /// True when <paramref name="port"/> holds a supplied value — including
        /// an explicit JSON <c>null</c>. False for a missing input
        /// (SPEC §A0.1, §0.6 SG-06).
        /// </summary>
        public abstract bool IsPresent(string port);

        /// <summary>
        /// True when <paramref name="port"/> holds a pending value: during a
        /// Dry Run the producing step was not executed, so the value exists as
        /// a placeholder carrying the producer's node id (SPEC §D8 item 1).
        /// An Operation skips inspection that depends on the concrete value.
        /// </summary>
        public abstract bool IsPending(string port);

        /// <summary>
        /// The token persisted by the last <see cref="StepResult.Waiting"/>
        /// and restored for the next <see cref="IOperation.Poll"/> — the only
        /// state a Waitable Operation keeps across a domain reload
        /// (SPEC §C2 item 4, §0.6 SG-06). <see langword="null"/> outside a
        /// resumed wait.
        /// </summary>
        public abstract JsonValue? ResumeToken { get; }

        /// <summary>The step's change recorder (SPEC §C2 item 6, §F7).</summary>
        public abstract IChangeRecorder Changes { get; }

        /// <summary>The step's structured log sink (SPEC §F6).</summary>
        public abstract IRunLogSink Log { get; }

        /// <summary>
        /// The raw outputs written through <see cref="SetOutput{T}"/> so far,
        /// keyed by port — the Core-internal collection seam the run loop
        /// reads after <see cref="IOperation.Execute"/>/<see cref="IOperation.Poll"/>
        /// before the values are validated, normalized and stored (SPEC §E5,
        /// §E4 step 7). It stays outside the public SG-06 surface so an
        /// external context implementation compiles without it; such an
        /// implementation's outputs simply never reach the runner.
        /// </summary>
        internal virtual IReadOnlyDictionary<string, JsonValue> Outputs => s_noOutputs;

        private static readonly IReadOnlyDictionary<string, JsonValue> s_noOutputs =
            ReadOnlyCollections.CopyMap(new Dictionary<string, JsonValue>(0));
    }
}
