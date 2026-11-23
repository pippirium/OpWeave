#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Compiler;
using OpWeave.Core.Persistence;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The per-Run value store of SPEC §E5: holds each step's outputs keyed by
    /// <see cref="ValueKey"/> so the inputs of later steps resolve from the
    /// Plan's <see cref="InputSource"/> records, and rebuilds from the
    /// <c>StepSucceeded</c> journal records alone — the journal is the single
    /// source of truth, so the same inputs are obtained after a reload
    /// (SPEC §E5 test, §F1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One stored value is at most <see cref="MaxValueBytes"/> of Canonical
    /// JSON UTF-8 (SPEC §E5: "1つの値は 256 KiB 以内"); an oversized value
    /// fails with OW-RUN-007. Values are stored as <see cref="JsonValue"/>
    /// only — a <see cref="Missing"/> input source is never stored, and never
    /// stored as a JSON <c>null</c> (SPEC §A0.1); an explicit JSON
    /// <c>null</c> written by an Operation is a present value like any other.
    /// Ref-typed values pass through as their JSON form; resolving the
    /// reference to a Unity object happens inside <c>ctx.Get&lt;T&gt;</c>
    /// through the G2 resolver (SPEC §E5, UnityAdapter — outside Core).
    /// </para>
    /// <para>
    /// <see cref="SetOutputs"/> and <see cref="Restore"/> are all-or-nothing:
    /// the outputs payload is inspected before any value is stored, so a
    /// rejected record leaves no partial state. Core runs on the main thread
    /// only, so no synchronization is taken (SPEC §0.4).
    /// </para>
    /// </remarks>
    internal sealed class ValueStore
    {
        /// <summary>The SPEC §E5 per-value bound: 256 KiB of Canonical JSON UTF-8.</summary>
        internal const int MaxValueBytes = 256 * 1024;

        private readonly Dictionary<ValueKey, JsonValue> _values = new Dictionary<ValueKey, JsonValue>();

        private ValueStore()
        {
        }

        /// <summary>An empty store: the state a Run starts with.</summary>
        internal static ValueStore Empty() => new ValueStore();

        /// <summary>The number of stored values.</summary>
        internal int Count => _values.Count;

        /// <summary>
        /// Rebuilds the store solely from the <c>StepSucceeded</c> outputs of
        /// a journal-loaded <see cref="RunSnapshot"/> (SPEC §E5, §F4 item 2):
        /// each <see cref="StepSnapshot.Outputs"/> object member becomes the
        /// <see cref="ValueKey"/>(0, 0, step index, port) value. A recorded
        /// outputs payload that is not a JSON object is recorded state the
        /// store cannot interpret — OW-STORE-002, the same fail-closed class
        /// the journal fold applies to uninterpretable required records — and
        /// an oversized member is OW-RUN-007; either leaves no partial store.
        /// </summary>
        /// <param name="types">Resolves the Plan's declared output types so the
        /// session discard walks Ref values and containers of Refs only —
        /// <c>core.Json</c> and other opaque data are never rewritten.</param>
        /// <param name="session">The current Editor session id each output's
        /// producing session is compared against; a null session disables the
        /// discard entirely.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="snapshot"/> or <paramref name="types"/> is null.
        /// </exception>
        internal static Result<ValueStore> Restore(
            RunSnapshot snapshot, ITypeRegistry types, string? session)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (types is null)
            {
                throw new ArgumentNullException(nameof(types));
            }
            var store = new ValueStore();
            var plan = snapshot.Plan;
            var steps = snapshot.Steps;
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].Outputs is { } outputs)
                {
                    outputs = StripStaleOutputs(plan, steps[i], types, session);
                    var applied = store.SetOutputs(steps[i].Index, outputs);
                    if (!applied.Ok)
                    {
                        return Result<ValueStore>.Failure(applied.Diagnostics);
                    }
                }
            }
            return Result<ValueStore>.Success(store);
        }

        // SPEC §F4 item 4's discard inside the rebuild: outputs the journal
        // shows produced in another Editor session lose their Ref 'handle'
        // keys at the declared Ref ports — every other member is opaque data
        // and survives verbatim. The provenance is the StepSucceeded
        // record's own session, never the run-level session of the last
        // record: a successful resume journals run_resumed under the new
        // session and must not revive keys that died with the old one.
        private static JsonValue StripStaleOutputs(
            CompiledPlan? plan, StepSnapshot snap, ITypeRegistry types, string? session)
        {
            var outputs = snap.Outputs!;
            if (session is null ||
                snap.OutputsSession is not { } produced ||
                string.Equals(produced, session, StringComparison.Ordinal) ||
                plan is null || snap.Index < 0 || snap.Index >= plan.Steps.Count)
            {
                return outputs;
            }
            return SessionHandleDiscard.StripMembers(
                outputs, plan.Steps[snap.Index].OutputTypes, types) ?? outputs;
        }

        /// <summary>
        /// Stores one output value. A value whose Canonical JSON UTF-8 form
        /// exceeds <see cref="MaxValueBytes"/> is rejected with OW-RUN-007 and
        /// nothing is stored (SPEC §E5).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        internal Result<Unit> Set(ValueKey key, JsonValue value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            if (ExceedsLimit(value))
            {
                return Result<Unit>.Failure(Oversized(key));
            }
            _values[key] = value;
            return Result<Unit>.Success(Unit.Value);
        }

        /// <summary>
        /// Stores every member of a step's <c>StepSucceeded</c> outputs object
        /// (<c>{port: value}</c>) under the step's <see cref="ValueKey"/>s —
        /// all-or-nothing (see the class remarks). The
        /// <paramref name="outputs"/> payload must be a JSON object; anything
        /// else is recorded state the store cannot interpret (OW-STORE-002).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="outputs"/> is null.</exception>
        internal Result<Unit> SetOutputs(int stepIndex, JsonValue outputs)
        {
            if (outputs is null)
            {
                throw new ArgumentNullException(nameof(outputs));
            }
            if (outputs.ToToken() is not JObject members)
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_STORE_002,
                    "The recorded step outputs are not a JSON object."));
            }
            var staged = new List<KeyValuePair<ValueKey, JsonValue>>(members.Count);
            foreach (var property in members.Properties())
            {
                var key = new ValueKey(0, 0, stepIndex, property.Name);
                var value = JsonValue.From(property.Value);
                if (ExceedsLimit(value))
                {
                    return Result<Unit>.Failure(Oversized(key));
                }
                staged.Add(new KeyValuePair<ValueKey, JsonValue>(key, value));
            }
            foreach (var pair in staged)
            {
                _values[pair.Key] = pair.Value;
            }
            return Result<Unit>.Success(Unit.Value);
        }

        /// <summary>
        /// The stored value, or <see cref="InputValue.Missing"/> — an absent
        /// key is never answered with a JSON <c>null</c> (SPEC §A0.1).
        /// </summary>
        internal InputValue Get(ValueKey key) =>
            _values.TryGetValue(key, out var value)
                ? InputValue.Present(value)
                : InputValue.Missing;

        /// <summary>
        /// Resolves every input port of <paramref name="step"/> from the
        /// Plan's <see cref="InputSource"/> records (SPEC §E5, §D7):
        /// <see cref="Constant"/>/<see cref="Default"/> supply their recorded
        /// value, <see cref="Missing"/> stays absent — never a JSON
        /// <c>null</c> (SPEC §A0.1), <see cref="FromInput"/> reads the named
        /// member of the Plan's execution inputs (absent member → absent
        /// input), and <see cref="FromStep"/> reads the producing step's
        /// stored output — an unrecorded one is OW-RUN-007 (SPEC §E5 "値の欠落").
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="step"/> or <paramref name="executionInputs"/> is null.
        /// </exception>
        internal Result<IReadOnlyDictionary<string, InputValue>> ResolveInputs(
            PlanStep step, JsonValue executionInputs)
        {
            if (step is null)
            {
                throw new ArgumentNullException(nameof(step));
            }
            if (executionInputs is null)
            {
                throw new ArgumentNullException(nameof(executionInputs));
            }
            var supplied = executionInputs.ToToken() as JObject;
            var resolved = new Dictionary<string, InputValue>(StringComparer.Ordinal);
            foreach (var pair in step.Inputs)
            {
                var input = Resolve(pair.Value, supplied);
                if (input is null)
                {
                    return Result<IReadOnlyDictionary<string, InputValue>>.Failure(
                        ErrorCodes.Create(
                            ErrorCodes.OW_RUN_007,
                            $"The output value of step {((FromStep)pair.Value).StepIndex} " +
                            $"port '{((FromStep)pair.Value).Port}' is not recorded.",
                            nodeId: step.NodeId,
                            port: pair.Key));
                }
                resolved[pair.Key] = input.Value;
            }
            return Result<IReadOnlyDictionary<string, InputValue>>.Success(
                ReadOnlyCollections.CopyMap(resolved));
        }

        // Resolves one InputSource; null return is the FromStep "recorded
        // value missing" case the caller turns into the attributed
        // OW-RUN-007.
        private InputValue? Resolve(InputSource source, JObject? executionInputs)
        {
            switch (source)
            {
                case Constant constant:
                    return InputValue.Present(constant.Value);
                case Default @default:
                    return InputValue.Present(@default.Value);
                case Missing _:
                    return InputValue.Missing;
                case FromInput fromInput:
                    return executionInputs is not null &&
                        executionInputs.TryGetValue(fromInput.InputId, out var value)
                            ? (InputValue?)InputValue.Present(JsonValue.From(value))
                            : InputValue.Missing;
                case FromStep fromStep:
                    var stored = Get(new ValueKey(0, 0, fromStep.StepIndex, fromStep.Port));
                    return stored.IsPresent ? stored : (InputValue?)null;
                default:
                    throw new ArgumentException(
                        "InputSource kind is outside the SPEC §D7 set.", nameof(source));
            }
        }

        private static bool ExceedsLimit(JsonValue value) =>
            Encoding.UTF8.GetByteCount(value.CanonicalText) > MaxValueBytes;

        private static Diagnostic Oversized(ValueKey key) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_007,
                $"The value of step {key.StepIndex} port '{key.Port}' exceeds the 256 KiB limit.",
                port: key.Port);
    }
}
