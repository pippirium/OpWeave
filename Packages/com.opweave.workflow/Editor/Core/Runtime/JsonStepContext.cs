#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The JSON-level <see cref="StepContext"/> implementation of SPEC §E5
    /// used by the Core run loop and Core tests (SPEC §0.6 SG-06): inputs are
    /// the resolved <see cref="InputValue"/>s the <see cref="ValueStore"/>
    /// produced from the Plan's InputSources, and outputs accumulate as raw
    /// <see cref="JsonValue"/>s until <see cref="StepOutputs.Collect"/>
    /// validates and normalizes them for the <c>StepSucceeded</c> record and
    /// the ValueStore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Get{T}"/> works at the JSON level: <c>Get&lt;JsonValue&gt;</c>
    /// returns the stored value verbatim — a Ref-typed value included, which
    /// stays JSON for the G2 resolver to materialize (SPEC §E5) — while any
    /// other <typeparamref name="T"/> is converted through the token's CLR
    /// representation. Reading a port that is missing or pending is a
    /// programming error (<see cref="InvalidOperationException"/>), matching
    /// the contract that an Operation checks <see cref="IsPresent"/>/
    /// <see cref="IsPending"/> first. A missing input is never answered with
    /// <see langword="null"/> (SPEC §A0.1); an explicit JSON <c>null</c>
    /// stays a present value.
    /// </para>
    /// <para>
    /// The <see cref="Changes"/> recorder and <see cref="Log"/> sink are
    /// injected by the caller — Core owns no §F7/§F6 persistence
    /// implementation — and <see cref="ResumeToken"/> carries the token of a
    /// resumed wait (SPEC §C2 item 4). This is a deliberately minimal,
    /// Unity-free implementation; the UnityAdapter supplies the CLR/Undo/
    /// Console one (SPEC §0.6 SG-06).
    /// </para>
    /// </remarks>
    internal sealed class JsonStepContext : StepContext, IRunResourceRegistry
    {
        private readonly IReadOnlyDictionary<string, InputValue> _inputs;
        private readonly IReadOnlyDictionary<string, string> _pending;
        private readonly IRunResourceRegistry? _resources;
        private readonly Dictionary<string, JsonValue> _outputs =
            new Dictionary<string, JsonValue>(StringComparer.Ordinal);

        /// <summary>
        /// Creates the context over one step's resolved
        /// <paramref name="inputs"/> (the <see cref="ValueStore.ResolveInputs"/>
        /// product). <paramref name="pendingProducers"/> marks the Dry-Run
        /// placeholder ports of SPEC §D8 item 1 — the producing step was not
        /// executed — mapping each port to its producing node id, which
        /// report <see cref="IsPending"/> and refuse <see cref="Get{T}"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="inputs"/>, <paramref name="changes"/> or
        /// <paramref name="log"/> is null.
        /// </exception>
        internal JsonStepContext(
            IReadOnlyDictionary<string, InputValue> inputs,
            IChangeRecorder changes,
            IRunLogSink log,
            JsonValue? resumeToken = null,
            IReadOnlyDictionary<string, string>? pendingProducers = null,
            IRunResourceRegistry? resources = null)
        {
            _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
            Changes = changes ?? throw new ArgumentNullException(nameof(changes));
            Log = log ?? throw new ArgumentNullException(nameof(log));
            ResumeToken = resumeToken;
            _resources = resources;
            _pending = pendingProducers is null
                ? ReadOnlyCollections.CopyMap(
                    Array.Empty<KeyValuePair<string, string>>())
                : ReadOnlyCollections.CopyMap(pendingProducers);
        }

        /// <summary>
        /// The outputs written through <see cref="SetOutput{T}"/> so far,
        /// keyed by port — the raw, unvalidated values
        /// <see cref="StepOutputs.Collect"/> turns into the persisted form.
        /// </summary>
        internal override IReadOnlyDictionary<string, JsonValue> Outputs => _outputs;

        public override JsonValue? ResumeToken { get; }

        public override IChangeRecorder Changes { get; }

        public override IRunLogSink Log { get; }

        /// <inheritdoc cref="StepContext.Get{T}"/>
        /// <exception cref="ArgumentNullException"><paramref name="port"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="port"/> is missing or pending.
        /// </exception>
        public override T Get<T>(string port)
        {
            if (port is null)
            {
                throw new ArgumentNullException(nameof(port));
            }
            if (_pending.TryGetValue(port, out var producer))
            {
                throw new InvalidOperationException(
                    $"Input port '{port}' is a pending Dry-Run value produced by node " +
                    $"'{producer}' and has no concrete value.");
            }
            if (!_inputs.TryGetValue(port, out var input) || !input.IsPresent)
            {
                throw new InvalidOperationException(
                    $"Input port '{port}' is missing.");
            }
            if (typeof(T) == typeof(JsonValue))
            {
                return (T)(object)input.Value!;
            }
            return input.Value!.ToToken().ToObject<T>()!;
        }

        /// <inheritdoc cref="StepContext.SetOutput{T}"/>
        /// <remarks>
        /// A <see cref="JsonValue"/> argument is stored verbatim; any other
        /// <typeparamref name="T"/> is converted through its CLR token. A
        /// <see langword="null"/> argument records an explicit JSON
        /// <c>null</c> — a present output value, never a missing one
        /// (SPEC §A0.1) — whose type conformance
        /// <see cref="StepOutputs.Collect"/> decides.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="port"/> is null.</exception>
        public override void SetOutput<T>(string port, T value)
        {
            if (port is null)
            {
                throw new ArgumentNullException(nameof(port));
            }
            _outputs[port] = value is JsonValue json
                ? json
                : JsonValue.From(value is null ? JValue.CreateNull() : JToken.FromObject(value));
        }

        /// <inheritdoc cref="StepContext.IsPresent"/>
        public override bool IsPresent(string port) =>
            port is not null &&
            (_pending.ContainsKey(port) ||
                (_inputs.TryGetValue(port, out var input) && input.IsPresent));

        /// <inheritdoc cref="StepContext.IsPending"/>
        public override bool IsPending(string port) =>
            port is not null && _pending.ContainsKey(port);

        /// <inheritdoc cref="IRunResourceRegistry.Acquire"/>
        /// <exception cref="NotSupportedException">
        /// The context carries no Run resource registry (SPEC §E6 item 4).
        /// </exception>
        public void Acquire(string kind, JsonValue? data) =>
            (_resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).Acquire(kind, data);

        /// <inheritdoc cref="IRunResourceRegistry.IsOpen"/>
        /// <exception cref="NotSupportedException">
        /// The context carries no Run resource registry (SPEC §E6 item 4).
        /// </exception>
        public RunResourceOpenState IsOpen(string kind, JsonValue? data) =>
            (_resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).IsOpen(kind, data);

        /// <inheritdoc cref="IRunResourceRegistry.IsHeldByRun"/>
        /// <exception cref="NotSupportedException">
        /// The context carries no Run resource registry (SPEC §E6 item 4).
        /// </exception>
        public bool IsHeldByRun(string kind, JsonValue? data) =>
            (_resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).IsHeldByRun(kind, data);

        /// <inheritdoc cref="IRunResourceRegistry.Release"/>
        /// <exception cref="NotSupportedException">
        /// The context carries no Run resource registry (SPEC §E6 item 4).
        /// </exception>
        public void Release(string kind, JsonValue? data) =>
            (_resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).Release(kind, data);

        /// <inheritdoc cref="IRunResourceRegistry.AcquisitionScope"/>
        /// <exception cref="NotSupportedException">
        /// The context carries no Run resource registry (SPEC §E6 item 4).
        /// </exception>
        public string AcquisitionScope =>
            (_resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).AcquisitionScope;
    }
}
