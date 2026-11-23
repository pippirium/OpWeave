#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The output-side half of SPEC §E5/§E4 step 7: once an Operation reports
    /// <see cref="Operations.StepResultKind.Succeeded"/>, every declared output
    /// port must hold a value that satisfies its Plan-resolved type — after
    /// which the value is normalized under the SPEC §B7.1 contract before it
    /// reaches the <c>StepSucceeded</c> journal record or the
    /// <see cref="ValueStore"/>, so what is persisted restores byte-identically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Collect"/> is all-or-nothing: the normalized
    /// <c>{port: value}</c> object exists only when every declared output is
    /// present and valid, so a failure leaves no partial
    /// <c>StepSucceeded</c> payload and no partial stored value. Every failure
    /// surfaces as OW-RUN-007 — the existing code SPEC §J assigns to a
    /// missing output, a type mismatch and the §E5 256 KiB bound, and the
    /// code SPEC §B7.1 item 4 assigns to Codec contract violations (rejection,
    /// exception, invalid or non-idempotent result) at output-save time; the
    /// sanitized detail stays in the diagnostic's <see cref="Diagnostic.Hint"/>
    /// and value/exception content never crosses (it may carry secrets).
    /// </para>
    /// <para>
    /// The type for validation is the Plan-recorded
    /// <see cref="PlanStep.OutputTypes"/> — the refinement-resolved Type ID
    /// the Runner is required to trust (SPEC §D7, §D4 item 6) — while the
    /// null and constraint contract comes from the verified Operation
    /// descriptor, resolved through the <see cref="IOperationSource"/> seam
    /// by the step's recorded (id, version): the identity the Runner's SPEC
    /// §1.4 fingerprint check binds to the Plan. Each output is validated and
    /// normalized under its declared <see cref="PortDescriptor.Nullable"/>
    /// and <see cref="PortDescriptor.Constraints"/> (SPEC §A5, §B0.1, §B7),
    /// so an explicit JSON <c>null</c> is accepted only for a
    /// <c>core.Json</c> output — where null is ordinary data — or a
    /// <c>Nullable=true</c> port. A step whose descriptor cannot be resolved,
    /// or a declared output the descriptor has no port for, fails closed as
    /// OW-RUN-007. Values set on ports the Plan does not declare are dropped
    /// from the persisted object.
    /// </para>
    /// </remarks>
    internal static class StepOutputs
    {
        /// <summary>
        /// Validates and normalizes one step's raw <paramref name="outputs"/>
        /// (the ports an Operation set through
        /// <see cref="Operations.StepContext.SetOutput{T}"/>) against
        /// <paramref name="step"/>'s declared <see cref="PlanStep.OutputTypes"/>
        /// under the <see cref="PortDescriptor.Nullable"/> and
        /// <see cref="PortDescriptor.Constraints"/> of the matching port of the
        /// Operation descriptor <paramref name="operations"/> resolves for the
        /// step's recorded (id, version), returning the normalized outputs
        /// object for the <c>StepSucceeded</c> record and the
        /// <see cref="ValueStore"/> — or a failure whose diagnostics are all
        /// OW-RUN-007.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="step"/>, <paramref name="types"/>,
        /// <paramref name="operations"/> or <paramref name="outputs"/> is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="step"/> carries an output Type ID that does not
        /// parse — a Plan defect, never a runtime condition on a
        /// hash-verified Plan.
        /// </exception>
        internal static Result<JsonValue> Collect(
            PlanStep step,
            ITypeRegistry types,
            IOperationSource operations,
            IReadOnlyDictionary<string, JsonValue> outputs)
        {
            if (step is null)
            {
                throw new ArgumentNullException(nameof(step));
            }
            if (types is null)
            {
                throw new ArgumentNullException(nameof(types));
            }
            if (operations is null)
            {
                throw new ArgumentNullException(nameof(operations));
            }
            if (outputs is null)
            {
                throw new ArgumentNullException(nameof(outputs));
            }

            // The verified descriptor the Runner's SPEC §1.4 fingerprint
            // check bound to this step's recorded (id, version); without it
            // no output port's Nullable/Constraints contract can be trusted,
            // so the step fails closed.
            var descriptor = operations.Find(step.OperationId, step.Version);
            if (descriptor is null)
            {
                return Result<JsonValue>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_007,
                    "The step's Operation descriptor cannot be resolved.",
                    nodeId: step.NodeId));
            }

            var normalized = new JObject();
            var failures = new List<Diagnostic>();
            foreach (var pair in step.OutputTypes)
            {
                var port = pair.Key;
                if (!outputs.TryGetValue(port, out var value) || value is null)
                {
                    failures.Add(Failure(
                        step, port, $"The output port '{port}' has no value.", null));
                    continue;
                }

                var declared = FindPort(descriptor, port);
                if (declared is null)
                {
                    failures.Add(Failure(
                        step, port,
                        $"The output port '{port}' is not declared by the Operation.",
                        null));
                    continue;
                }

                var parsed = TypeRef.Parse(pair.Value);
                if (!parsed.Ok)
                {
                    throw new InvalidOperationException(
                        $"Plan step '{step.NodeId}' carries the unparseable output Type ID '{pair.Value}'.");
                }
                var type = parsed.Value!;

                var validation = ValueValidation.Validate(
                    types, type, value, declared.Constraints, declared.Nullable);
                if (validation.Count != 0)
                {
                    failures.Add(Failure(
                        step, port,
                        $"The output port '{port}' value does not satisfy its declared type.",
                        RejectionHint(validation[0])));
                    continue;
                }

                var form = ValueValidation.Normalize(
                    types, type, value, declared.Constraints, declared.Nullable);
                if (!form.Ok)
                {
                    failures.Add(Failure(
                        step, port,
                        $"The output port '{port}' value does not satisfy its declared type.",
                        form.Diagnostics[0].Message));
                    continue;
                }
                var persisted = form.Value!;

                if (Encoding.UTF8.GetByteCount(persisted.CanonicalText) > ValueStore.MaxValueBytes)
                {
                    failures.Add(Failure(
                        step, port,
                        $"The output port '{port}' value exceeds the 256 KiB limit.", null));
                    continue;
                }
                normalized[port] = persisted.ToToken();
            }

            return failures.Count == 0
                ? Result<JsonValue>.Success(JsonValue.From(normalized))
                : Result<JsonValue>.Failure(failures);
        }

        // The output port of the resolved descriptor, or null when the
        // descriptor declares none — impossible on a fingerprint-verified
        // registry, but treated as an unobtainable port contract and failed
        // closed rather than silently validated unconstrained.
        private static PortDescriptor? FindPort(OperationDescriptor descriptor, string name)
        {
            var outputs = descriptor.Outputs;
            for (var i = 0; i < outputs.Count; i++)
            {
                if (string.Equals(outputs[i].Name, name, StringComparison.Ordinal))
                {
                    return outputs[i];
                }
            }
            return null;
        }

        // A Codec's Validate diagnostics reach Collect verbatim — only the
        // SPEC §B7.1 item 4 contract breaches are sanitized inside
        // ValueValidation — and a Codec may build their text from the
        // rejected value, which may carry secrets. No codec-supplied field
        // crosses into the emitted diagnostic; the one detail kept is the
        // code, and only when it is a published SPEC §J code.
        private static string RejectionHint(Diagnostic rejection) =>
            ErrorCodes.IsPublished(rejection.Code)
                ? $"The declared type rejected the value ({rejection.Code})."
                : "The declared type rejected the value.";

        private static Diagnostic Failure(PlanStep step, string port, string message, string? hint) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_007, message, nodeId: step.NodeId, port: port, hint: hint);
    }
}
