#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The D3 operation-validation phase (SPEC §D3): resolves each node's
    /// (id, version) in the SPEC §C3 <see cref="RegistrySnapshot"/>, checks
    /// that every parameter key names an input port, that every parameter
    /// value satisfies the port's type and <see cref="PortConstraints"/>, and
    /// calls <see cref="IOperation.ValidateStatic"/> last.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution outcomes follow SPEC §D3: an id with no registered version
    /// is OW-OP-001 — with the registry diff's previous definition source in
    /// the hint when the Operation was removed (SPEC §C3 rule 7; the
    /// persisted-diff producer is T-W2.2-04), and with the
    /// "this OpWeave version does not support branching" hint for an unknown
    /// reserved <c>core.*</c> id (SPEC §N.3 item 3). An id registered only at
    /// other versions is OW-OP-002 — a hint proposing
    /// <c>migrate_workflow</c> for an older node version (SPEC §A7 item 3);
    /// validation never applies migrations itself.
    /// </para>
    /// <para>
    /// Parameter values are checked in two layers matching the SPEC §J code
    /// split: the type's Codec (SPEC §B7, called without port constraints)
    /// produces OW-TYPE-003 for the type check, while the port's
    /// <see cref="PortConstraints"/> surface as OW-OP-006 — an enum violation
    /// carries the allowed-values list in the hint (SPEC §D3 test). The phase
    /// evaluates the primitive keywords itself, with the same semantics the
    /// built-in codecs document in <see cref="CodecConstraints"/>: enum
    /// membership is a closed set of strings, pattern is an unanchored
    /// search, MaxLength counts Unicode code points, numeric bounds compare
    /// exactly, and keywords apply only to the JSON kinds they can constrain.
    /// The Codec then sees the real constraints too — SPEC §B7 exposes
    /// <see cref="PortConstraints"/> to <see cref="IValueCodec.Validate"/>, and
    /// a custom Codec can enforce constraint rules on its type's structured
    /// representation the primitive keyword checks cannot express. A codec
    /// diagnostic the keyword evaluation did not already cover is remapped to
    /// OW-OP-006 rather than silently accepted. A value the codec rejects at
    /// type level is not constraint-checked: its OW-TYPE-003 is the
    /// meaningful finding. The <see cref="PortConstraints.ConstantOnly"/>
    /// keyword is not a parameter-value constraint (a constant is the only
    /// supplier it admits; the edge/binding check is D4's).
    /// </para>
    /// <para>
    /// <see cref="IOperation.ValidateStatic"/> runs last for the node
    /// (SPEC §D3). Running it needs two things Core cannot supply itself: an
    /// implementation instance on every snapshot entry and a
    /// <see cref="StaticContext"/> factory (SPEC §0.6 SG-06: Core hosts no
    /// context implementation; UnityAdapter and test fixtures provide it).
    /// SPEC §J assigns no diagnostic to a compiler or snapshot that cannot
    /// run the static check — OW-OP-008 names a registration-time
    /// instantiation failure — so both are construction requirements: a null
    /// factory fails with <see cref="ArgumentNullException"/> and an
    /// implementation-less snapshot entry with <see cref="ArgumentException"/>,
    /// making a phase that would silently skip the check unconstructable
    /// rather than misreporting the workflow under a published code. The
    /// context exposes every declared input port
    /// with the codec-resolved parameter value — or the written one when the
    /// Resolve stage produced none — and <see cref="InputValue.Missing"/> for
    /// unsupplied ports. An implementation that throws is reported as
    /// OW-RUN-006 (SPEC §0.6 SG-06: operation exceptions are OW-RUN-006)
    /// without exception text, which may carry secrets.
    /// </para>
    /// </remarks>
    internal sealed class OperationValidationPhase : IValidationPhase
    {
        private readonly RegistrySnapshot _snapshot;
        private readonly IReadOnlyList<RegistryChange>? _changes;
        private readonly Func<string, IReadOnlyDictionary<string, InputValue>, StaticContext> _staticContextFactory;

        internal OperationValidationPhase(
            RegistrySnapshot snapshot,
            Func<string, IReadOnlyDictionary<string, InputValue>, StaticContext> staticContextFactory,
            IReadOnlyList<RegistryChange>? changes = null)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _staticContextFactory =
                staticContextFactory ?? throw new ArgumentNullException(nameof(staticContextFactory));
            _changes = changes;
            for (var i = 0; i < snapshot.Operations.Count; i++)
            {
                var operation = snapshot.Operations[i];
                if (operation.Implementation is null)
                {
                    // No published code describes a registered Operation
                    // without an implementation instance, so the malformed
                    // snapshot is rejected here instead of surfacing a
                    // diagnostic that names the wrong cause.
                    throw new ArgumentException(
                        $"The snapshot entry for operation '{operation.Descriptor.Id}' version " +
                            $"{operation.Descriptor.Version} carries no implementation instance, " +
                            "so its static check cannot run.",
                        nameof(snapshot));
                }
            }
        }

        public ValidationPhaseKind Kind => ValidationPhaseKind.D3;

        public void Run(ValidationContext context)
        {
            var nodes = context.Document.Nodes;
            var indexOf = new Dictionary<NodeModel, int>();
            for (var i = 0; i < nodes.Count; i++)
            {
                // Duplicate node ids are the D2 phase's OW-WF-004 finding; the
                // first occurrence wins the document slot, matching
                // ValidationContext's node-order rule.
                if (!indexOf.ContainsKey(nodes[i]))
                {
                    indexOf.Add(nodes[i], i);
                }
            }

            foreach (var node in context.ActiveNodes)
            {
                CheckNode(context, node, indexOf[node]);
            }
        }

        private void CheckNode(ValidationContext context, NodeModel node, int index)
        {
            var path = "$.nodes[" + index + "]";

            var registered = _snapshot.Find(node.Operation, node.Version);
            if (registered is null)
            {
                ReportUnresolvable(context, node, path);
                return;
            }

            // SPEC §N.5.5: v1 never downgrades a blueprint-kind Operation to
            // native — requesting its execution is OW-OP-010.
            var descriptor = registered.Descriptor;
            if (descriptor.Kind == OperationKind.Blueprint)
            {
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_OP_010,
                    $"Operation '{node.Operation}' declares kind 'blueprint', which v1 cannot register or execute.",
                    nodeId: node.Id,
                    jsonPath: path + ".operation"));
                return;
            }

            CheckParameters(context, node, index, descriptor);
            ValidateStatic(context, node, path, registered);
        }

        // (id, version) resolution failure: OW-OP-001 for an id with no
        // registered version, OW-OP-002 for a registered id at a different
        // version (SPEC §D3).
        private void ReportUnresolvable(ValidationContext context, NodeModel node, string path)
        {
            var latest = _snapshot.FindLatest(node.Operation);
            if (latest is not null)
            {
                string hint;
                if (node.Version < latest.Descriptor.Version)
                {
                    // SPEC §A7 item 3: suggest migration; validation never
                    // applies migrations itself.
                    hint = $"Run 'opweave migrate_workflow' to migrate the workflow to version {latest.Descriptor.Version}.";
                }
                else
                {
                    var versions = new List<int>();
                    foreach (var operation in _snapshot.Operations)
                    {
                        if (string.Equals(
                                operation.Descriptor.Id, node.Operation, StringComparison.Ordinal))
                        {
                            versions.Add(operation.Descriptor.Version);
                        }
                    }
                    hint = "The operation is registered at version(s) " +
                        string.Join(", ", versions) + ".";
                }
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_OP_002,
                    $"Operation '{node.Operation}' does not have a registered version {node.Version}.",
                    nodeId: node.Id,
                    jsonPath: path + ".operation",
                    hint: hint));
                return;
            }

            context.Report(ErrorCodes.Create(
                ErrorCodes.OW_OP_001,
                $"Unknown operation '{node.Operation}'.",
                nodeId: node.Id,
                jsonPath: path + ".operation",
                hint: UnknownOperationHint(node)));
        }

        // SPEC §D3 / §C3 rule 7: a removed custom Operation's previous
        // definition source travels with the registry diff; an unknown
        // reserved core.* id gets the SPEC §N.3 item 3 branching hint.
        private string UnknownOperationHint(NodeModel node)
        {
            if (node.Operation.StartsWith("core.", StringComparison.Ordinal))
            {
                return "This version of OpWeave does not support branching or other control-flow Operations.";
            }

            var sourceFile = PreviousSource(node);
            return sourceFile is not null
                ? $"The operation was previously defined in '{sourceFile}'."
                : "Register the operation before validating, or fix the operation id.";
        }

        // The latest registry diff's previous definition source for a removed
        // Operation: prefer a record matching the node's version, otherwise
        // the removed record with the highest recorded version.
        private string? PreviousSource(NodeModel node)
        {
            if (_changes is null)
            {
                return null;
            }
            RegistryChange? best = null;
            for (var i = 0; i < _changes.Count; i++)
            {
                var change = _changes[i];
                if (change.Kind != RegistryChangeKinds.Removed ||
                    !string.Equals(change.OperationId, node.Operation, StringComparison.Ordinal))
                {
                    continue;
                }
                if (change.Version == node.Version)
                {
                    return change.SourceFile;
                }
                if (best is null || change.Version > best.Version)
                {
                    best = change;
                }
            }
            return best?.SourceFile;
        }

        // Parameter keys must be input ports (OW-OP-005); each present value
        // is checked by type (OW-TYPE-003 through the Codec) and by
        // PortConstraints (OW-OP-006) (SPEC §D3).
        private void CheckParameters(
            ValidationContext context, NodeModel node, int index, OperationDescriptor descriptor)
        {
            var portsPath = "$.nodes[" + index + "].parameters";
            var ports = new Dictionary<string, PortDescriptor>(StringComparer.Ordinal);
            foreach (var input in descriptor.Inputs)
            {
                // Duplicate port names are the registration-time OW-OP-003
                // check; the first wins here.
                if (!ports.ContainsKey(input.Name))
                {
                    ports.Add(input.Name, input);
                }
            }

            foreach (var pair in node.Parameters)
            {
                var memberPath = StructuralValidationPhase.MemberPath(portsPath, pair.Key);
                if (!ports.TryGetValue(pair.Key, out var port))
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_OP_005,
                        $"Parameter '{pair.Key}' is not an input port of operation '{node.Operation}' version {node.Version}.",
                        nodeId: node.Id,
                        port: pair.Key,
                        jsonPath: memberPath,
                        hint: descriptor.Inputs.Count == 0
                            ? "The operation declares no input ports."
                            : "Input ports of '" + node.Operation + "': " +
                                string.Join(", ", ports.Keys) + "."));
                    continue;
                }
                CheckValue(context, node, memberPath, pair.Key, pair.Value, port);
            }
        }

        // The value layers of SPEC §D3: the Codec's type check first — run
        // without port constraints so OW-TYPE-003 and other codec diagnostics
        // cross verbatim — then the port's PortConstraints as OW-OP-006. A
        // mistyped value is not constraint-checked — its OW-TYPE-003 is the
        // meaningful finding.
        private void CheckValue(
            ValidationContext context,
            NodeModel node,
            string memberPath,
            string portName,
            JsonValue value,
            PortDescriptor port)
        {
            var parsed = TypeRef.Parse(port.Type);
            if (!parsed.Ok)
            {
                // A malformed port type is the registration-time OW-OP-003
                // check's finding; a validated snapshot cannot carry one.
                return;
            }

            var diagnostics = ValueValidation.Validate(
                _snapshot.Types, parsed.Value!, value, null, port.Nullable);
            if (diagnostics.Count != 0)
            {
                ReportAt(context, diagnostics, node.Id, portName, memberPath);
                return;
            }

            CheckConstraints(context, node, memberPath, portName, value, port, parsed.Value!);
        }

        // OW-OP-006 per violated PortConstraints keyword. The phase evaluates
        // the primitive keywords itself — mirroring CodecConstraints'
        // documented semantics so the enum violation's hint can list the
        // allowed values (SPEC §D3 test) — then hands the real constraints to
        // the Codec once more: SPEC §B7 exposes PortConstraints to
        // IValueCodec.Validate, and a custom Codec can enforce constraint
        // rules on its type's structured representation the keyword checks
        // cannot express. The constraint-free Codec call already passed, so
        // every diagnostic this second call returns is a constraint
        // violation; ones the keyword evaluation did not already produce are
        // remapped to OW-OP-006 rather than silently accepted. A sanctioned
        // explicit null (Nullable port, non-core.Json) constrains nothing and
        // never reaches the Codec.
        private void CheckConstraints(
            ValidationContext context,
            NodeModel node,
            string memberPath,
            string portName,
            JsonValue value,
            PortDescriptor port,
            TypeRef type)
        {
            var constraints = port.Constraints;
            if (constraints is null)
            {
                return;
            }
            var token = value.ToToken();
            if (token.Type == JTokenType.Null && !ValueValidation.IsCoreJson(type))
            {
                return;
            }

            var violations = new List<Diagnostic>();
            void Violation(string message, string? hint) => violations.Add(ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                $"Parameter '{portName}' {message}",
                nodeId: node.Id,
                port: portName,
                jsonPath: memberPath,
                hint: hint));

            if (constraints.Enum is not null && !IsAllowedEnumValue(token, constraints.Enum))
            {
                var allowed = new List<string>(constraints.Enum.Count);
                foreach (var entry in constraints.Enum)
                {
                    allowed.Add("\"" + entry + "\"");
                }
                Violation(
                    "value is not in the allowed set of values.",
                    "Allowed values: " + string.Join(", ", allowed) + ".");
            }

            switch (token.Type)
            {
                case JTokenType.String:
                {
                    var text = (string)((JValue)token).Value!;
                    if (constraints.Pattern is not null && !MatchesPattern(text, constraints.Pattern))
                    {
                        Violation(
                            "value does not match the required pattern.",
                            $"Required pattern: {constraints.Pattern}.");
                    }
                    if (constraints.MaxLength is int maxLength && ExceedsMaxLength(text, maxLength))
                    {
                        Violation(
                            "value exceeds the maximum length.",
                            $"Maximum length: {maxLength.ToString(CultureInfo.InvariantCulture)}.");
                    }
                    break;
                }
                case JTokenType.Integer:
                {
                    var integer = (long)((JValue)token).Value!;
                    if (IsBound(constraints.Min, out var min) &&
                        CompareIntegerToBound(integer, min) < 0)
                    {
                        Violation(
                            "value is below the minimum bound.",
                            $"Minimum: {min.ToString(CultureInfo.InvariantCulture)}.");
                    }
                    if (IsBound(constraints.Max, out var max) &&
                        CompareIntegerToBound(integer, max) > 0)
                    {
                        Violation(
                            "value is above the maximum bound.",
                            $"Maximum: {max.ToString(CultureInfo.InvariantCulture)}.");
                    }
                    break;
                }
                case JTokenType.Float:
                {
                    var number = (double)((JValue)token).Value!;
                    if (IsBound(constraints.Min, out var min) && number < min)
                    {
                        Violation(
                            "value is below the minimum bound.",
                            $"Minimum: {min.ToString(CultureInfo.InvariantCulture)}.");
                    }
                    if (IsBound(constraints.Max, out var max) && number > max)
                    {
                        Violation(
                            "value is above the maximum bound.",
                            $"Maximum: {max.ToString(CultureInfo.InvariantCulture)}.");
                    }
                    break;
                }
            }

            for (var i = 0; i < violations.Count; i++)
            {
                context.Report(violations[i]);
            }
            if (violations.Count != 0)
            {
                // The keyword findings already cover the semantics the
                // built-in Codecs mirror, so a second Codec verdict on the
                // same constraints adds nothing new to report.
                return;
            }

            var codecDiagnostics = ValueValidation.Validate(
                _snapshot.Types, type, value, constraints, port.Nullable);
            if (codecDiagnostics.Count != 0)
            {
                ReportConstraintViolations(
                    context, codecDiagnostics, node.Id, portName, memberPath);
            }
        }

        // Remaps a Codec-produced constraint diagnostic to the OW-OP-006 the
        // SPEC §J code split demands at the port position — the same
        // node/port/JsonPath attribution ReportAt applies, with the published
        // OW-OP-006 severity and retryability no matter what the Codec
        // emitted. A contract-breaching Codec returning a message-less
        // diagnostic degrades to fixed text instead of throwing across the
        // layer boundary (SPEC §0.4).
        private static void ReportConstraintViolations(
            ValidationContext context,
            IReadOnlyList<Diagnostic> diagnostics,
            string nodeId,
            string port,
            string path)
        {
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var diagnostic = diagnostics[i];
                var jsonPath =
                    diagnostic.JsonPath is not null &&
                    diagnostic.JsonPath.StartsWith("$", StringComparison.Ordinal)
                        ? path + diagnostic.JsonPath.Substring(1)
                        : path;
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_OP_006,
                    string.IsNullOrEmpty(diagnostic.Message)
                        ? $"Parameter '{port}' violates the port's constraints."
                        : $"Parameter '{port}' violates the port's constraints: {diagnostic.Message}",
                    nodeId: diagnostic.NodeId ?? nodeId,
                    port: diagnostic.Port ?? port,
                    jsonPath: jsonPath,
                    hint: diagnostic.Hint));
            }
        }

        // SPEC §D3: IOperation.ValidateStatic runs last. The context exposes
        // every declared input port — the codec-resolved value when the
        // Resolve stage produced one, else the written token, else Missing.
        // The resolved values come from the per-node map rather than the
        // complete ResolvedWorkflow, so a conversion failure on another node
        // — which keeps that object from being produced while excluding only
        // the failed node (SPEC §D1 item 2) — cannot strip this node's
        // normalized values.
        private void ValidateStatic(
            ValidationContext context, NodeModel node, string path, RegisteredOperation registered)
        {
            // Constructor-checked invariant: every snapshot entry carries its
            // implementation instance, so the slot is always populated here.
            var implementation = registered.Implementation!;

            var resolved = context.ResolvedParameters is not null &&
                context.ResolvedParameters.TryGetValue(node.Id, out var resolvedParameters)
                    ? resolvedParameters
                    : null;
            var parameters = new Dictionary<string, InputValue>(StringComparer.Ordinal);
            foreach (var input in registered.Descriptor.Inputs)
            {
                JsonValue? value = null;
                if (resolved is not null && resolved.TryGetValue(input.Name, out var resolvedValue))
                {
                    value = resolvedValue;
                }
                else if (node.Parameters.TryGetValue(input.Name, out var written))
                {
                    value = written;
                }
                parameters[input.Name] =
                    value is null ? InputValue.Missing : InputValue.Present(value);
            }

            IReadOnlyList<Diagnostic> diagnostics;
            try
            {
                diagnostics = implementation.ValidateStatic(
                    new StaticTypedContext(
                        _staticContextFactory(node.Id, parameters),
                        StaticTypedContext.StaticTypesFor(
                            context, node, registered.Descriptor)));
            }
            catch (Exception)
            {
                // SPEC §0.6 SG-06: operation exceptions surface as OW-RUN-006;
                // the exception text may carry secrets and never crosses.
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    $"Operation '{node.Operation}' threw an exception during static validation.",
                    nodeId: node.Id));
                return;
            }
            if (diagnostics is null)
            {
                return;
            }
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var diagnostic = diagnostics[i];
                if (diagnostic is null)
                {
                    continue;
                }
                context.Report(diagnostic with { NodeId = diagnostic.NodeId ?? node.Id });
            }
        }

        // Attributes a codec-produced diagnostic to the value's document
        // position — the same composition the Resolve stage applies.
        private static void ReportAt(
            ValidationContext context,
            IReadOnlyList<Diagnostic> diagnostics,
            string nodeId,
            string port,
            string path)
        {
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var diagnostic = diagnostics[i];
                var jsonPath =
                    diagnostic.JsonPath is not null &&
                    diagnostic.JsonPath.StartsWith("$", StringComparison.Ordinal)
                        ? path + diagnostic.JsonPath.Substring(1)
                        : path;
                context.Report(diagnostic with
                {
                    NodeId = diagnostic.NodeId ?? nodeId,
                    Port = diagnostic.Port ?? port,
                    JsonPath = jsonPath,
                });
            }
        }

        // ── Constraint keyword evaluation, mirroring CodecConstraints ──────

        private static bool IsAllowedEnumValue(JToken token, IReadOnlyList<string> allowed)
        {
            if (token.Type != JTokenType.String)
            {
                return false;
            }
            var text = (string)((JValue)token).Value!;
            for (var i = 0; i < allowed.Count; i++)
            {
                if (string.Equals(allowed[i], text, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MatchesPattern(string text, string pattern)
        {
            try
            {
                return Regex.IsMatch(text, pattern);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool ExceedsMaxLength(string text, int maxLength)
        {
            var length = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) &&
                    i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                }
                if (++length > maxLength)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsBound(double? bound, out double value)
        {
            value = bound ?? 0.0;
            return bound.HasValue && !double.IsNaN(value);
        }

        private const double TwoTo63 = 9223372036854775808.0;

        private static int CompareIntegerToBound(long value, double bound)
        {
            if (bound >= TwoTo63)
            {
                return -1;
            }
            if (bound < -TwoTo63)
            {
                return 1;
            }
            var whole = (long)Math.Truncate(bound);
            if (value != whole)
            {
                return value < whole ? -1 : 1;
            }
            var fraction = bound - whole;
            return fraction > 0.0 ? -1 : (fraction < 0.0 ? 1 : 0);
        }
    }
}
