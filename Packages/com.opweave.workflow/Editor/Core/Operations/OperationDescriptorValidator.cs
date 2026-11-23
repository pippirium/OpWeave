#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The registration-time Descriptor checks of SPEC §C3 rule 3 that need only
    /// the Descriptor and the type snapshot: an <see cref="OperationDescriptor.Id"/>
    /// outside the SPEC §A4 Operation id grammar, duplicate port names,
    /// unresolvable port types (OW-OP-014 per SPEC §C3 step 6 "未知の入出力型"),
    /// side effects outside the closed SPEC §C4 vocabulary, English
    /// <see cref="OperationDescriptor.Summary"/> over 120 characters, and
    /// <see cref="OperationDescriptor.ControlOutputs"/> other than
    /// <c>["next"]</c> — each reported as OW-OP-003 unless stated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="OperationKind.Blueprint"/> is reported as OW-OP-010 (SPEC
    /// §N.5.5): blueprint registration and execution stay disabled in v1 and
    /// are never downgraded.
    /// </para>
    /// <para>
    /// The remaining rule-3 check — a Waitable Operation without a Poll
    /// implementation — needs the implementation class, not the Descriptor,
    /// and belongs to the snapshot builder (T-W1.5-04).
    /// </para>
    /// <para>
    /// Diagnostics are collected in a fixed order (id grammar, kind,
    /// duplicate ports, port types, side effects, summary, control outputs)
    /// so the same input always reports the same list; every violation yields
    /// its own <see cref="Diagnostic"/> rather than stopping at the first.
    /// </para>
    /// </remarks>
    public static class OperationDescriptorValidator
    {
        /// <summary>The maximum English summary length (SPEC §C1).</summary>
        public const int SummaryMaxLength = 120;

        /// <summary>
        /// Validates <paramref name="descriptor"/> against the type snapshot.
        /// Returns an empty list when the Descriptor is clean; otherwise one
        /// diagnostic per violation.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="descriptor"/> or <paramref name="types"/> is null.
        /// </exception>
        public static IReadOnlyList<Diagnostic> Validate(
            OperationDescriptor descriptor, ITypeRegistry types)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }
            if (types is null)
            {
                throw new ArgumentNullException(nameof(types));
            }

            var diagnostics = new List<Diagnostic>();

            // The publishable form of the Descriptor-supplied Operation ID:
            // a malformed ID is never interpolated verbatim — it may carry
            // credentials (SPEC §C3 error paragraph).
            var safeId = PublishedId.OperationId(descriptor.Id);

            // ── Operation id grammar (SPEC §A4, §C3 rule 3) ─────────────────
            // A Descriptor whose id violates the grammar can never be
            // referenced from a valid workflow, so it is an invalid
            // Descriptor (OW-OP-003). Registration and the workflow parser
            // share the single grammar in IdGrammar.
            if (!IdGrammar.IsValidOperationId(descriptor.Id))
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_003,
                    $"Operation '{safeId}' has an id that does not match the SPEC §A4 Operation id grammar.",
                    hint: $"Expected ^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$ (two or more dot-separated segments), at most {IdGrammar.OperationIdMaxLength} characters."));
            }

            // ── Reserved kind discriminator (SPEC §N.5.5, §X.10.4) ────────────
            if (descriptor.Kind == OperationKind.Blueprint)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_010,
                    $"Operation '{safeId}' declares kind 'blueprint', which v1 cannot register or execute."));
            }
            else if (!Enum.IsDefined(typeof(OperationKind), descriptor.Kind))
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_003,
                    $"Operation '{safeId}' declares an unrecognized kind."));
            }

            // ── Duplicate port names (SPEC §C3 rule 3) ────────────────────────
            CheckDuplicatePorts(diagnostics, descriptor.Inputs, "input");
            CheckDuplicatePorts(diagnostics, descriptor.Outputs, "output");

            // ── Port type resolution (SPEC §C3 rule 3) ────────────────────────
            CheckPortTypes(diagnostics, types, descriptor.Inputs, "input");
            CheckPortTypes(diagnostics, types, descriptor.Outputs, "output");

            // ── Closed side-effect vocabulary (SPEC §C4) ──────────────────────
            var effects = descriptor.SideEffects;
            for (var i = 0; i < effects.Count; i++)
            {
                if (!EffectNames.IsKnown(effects[i]))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_003,
                        $"Operation '{safeId}' declares unknown side effect '{effects[i]}'.",
                        hint: "Use the SPEC §C4 vocabulary (" +
                            string.Join(", ", EffectNames.All) + ")."));
                }
            }

            // ── English summary length (SPEC §C1) ─────────────────────────────
            if (descriptor.Summary.Length > SummaryMaxLength)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_003,
                    $"Operation '{safeId}' has a summary of {descriptor.Summary.Length} characters, over the {SummaryMaxLength}-character limit."));
            }

            // ── Control outputs reserved in v1 (SPEC §C1 item 2, appendix N) ──
            var controlOutputs = descriptor.ControlOutputs;
            if (controlOutputs.Count != 1 ||
                !string.Equals(controlOutputs[0], "next", StringComparison.Ordinal))
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_003,
                    $"Operation '{safeId}' declares control outputs other than [\"next\"], which is reserved for future control flow."));
            }

            return diagnostics;
        }

        private static void CheckDuplicatePorts(
            List<Diagnostic> diagnostics,
            IReadOnlyList<PortDescriptor> ports,
            string direction)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < ports.Count; i++)
            {
                if (!seen.Add(ports[i].Name))
                {
                    // A port name is an unvalidated scoped ID — publish its
                    // safe form, never the raw text.
                    var safeName = PublishedId.ScopedName(ports[i].Name);
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_003,
                        $"Duplicate {direction} port name '{safeName}'.",
                        port: safeName));
                }
            }
        }

        private static void CheckPortTypes(
            List<Diagnostic> diagnostics,
            ITypeRegistry types,
            IReadOnlyList<PortDescriptor> ports,
            string direction)
        {
            for (var i = 0; i < ports.Count; i++)
            {
                var port = ports[i];
                // Both fields are unvalidated provider text: the port name
                // is a scoped ID and the type text is a §B1 TypeRef —
                // publish their safe forms, never a malformed raw string.
                var safeName = PublishedId.ScopedName(port.Name);
                var safeType = PublishedId.PortType(port.Type);
                var parsed = TypeRef.Parse(port.Type);
                if (!parsed.Ok)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_003,
                        $"The {direction} port '{safeName}' has a malformed type ID '{safeType}'.",
                        port: safeName,
                        hint: parsed.Diagnostics[0].Hint));
                    continue;
                }
                var resolved = types.Resolve(parsed.Value!);
                if (!resolved.Ok)
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_014,
                        $"The {direction} port '{safeName}' references unresolvable type '{safeType}'.",
                        port: safeName,
                        hint: resolved.Diagnostics[0].Message));
                }
            }
        }
    }
}
