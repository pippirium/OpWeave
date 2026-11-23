#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The Build Plan stage (SPEC §N.5.5 item 4, §D7): turns a validated
    /// workflow into the immutable <see cref="CompiledPlan"/> whose
    /// <see cref="CompiledPlan.PlanHash"/> fixes its execution meaning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// planHash is the SHA-256 of the canonical SG-12 input object with
    /// exactly twelve named fields — <c>canonicalJsonVersion</c>,
    /// <c>resolvedWorkflow</c>, <c>executionInputs</c>, <c>steps</c>,
    /// <c>transitions</c>, <c>retry</c>, <c>timeouts</c>,
    /// <c>requiredEffects</c>, <c>usedOperationFingerprints</c>,
    /// <c>usedTypeFingerprints</c>, <c>usedCodecFingerprints</c> and
    /// <c>environmentIdentifiers</c> (SPEC §0.6 SG-12, §D0.1). PlanId,
    /// timestamps, layout, WorkflowHash and registryHash never enter it.
    /// </para>
    /// <para>
    /// Local, reversible choices where SPEC fixes the twelve names but not
    /// their inner shapes (frozen by the golden vectors of
    /// local:T-W1.6-07:plan-hash): <c>steps</c>, <c>transitions</c>,
    /// <c>retry</c> and <c>timeouts</c> are arrays aligned by step index — a
    /// step carries <c>index</c>, <c>nodeId</c>, <c>operationId</c>,
    /// <c>version</c>, the snake_case <c>idempotency</c>, its input-port
    /// SG-12 source tags and its resolved output Type IDs;
    /// <c>transitions</c> entries are the port→next-step objects;
    /// <c>requiredEffects</c> holds per-step actual effects plus the
    /// approvals; <c>executionInputs</c> covers every declared WF input with
    /// an unambiguous tag — a supplied one as
    /// <c>{"kind":"supplied","value":&lt;resolved value&gt;}</c> and an absent
    /// one as the SG-12 <c>{"kind":"missing"}</c>, so a supplied
    /// <c>core.Json</c> value shaped like a sentinel still hashes distinctly
    /// from Missing (§0.6 SG-12) — undeclared members carry no execution
    /// meaning (OW-VAL-014) and are excluded;
    /// <c>usedOperationFingerprints</c> sorts by (id, version),
    /// <c>usedTypeFingerprints</c> by typeId and
    /// <c>usedCodecFingerprints</c> by the Codec ID (SHA-256 of each SG-11
    /// identity object), all Ordinal; the approval detail is the
    /// Operation's <c>"id vN"</c> (SPEC §H4 item 3).
    /// </para>
    /// <para>
    /// The environment identifiers and per-Type/Codec fingerprint atoms are
    /// supplied by the injected <see cref="IPlanProvenance"/>. Its configured
    /// <see cref="IntegrityLevel"/> is an explicit Build Plan input evaluated
    /// first: <see cref="IntegrityLevel.Full"/> is rejected with OW-OP-011
    /// before any Plan is produced, never downgraded to weak (SPEC §0.1
    /// F-05); a failed <see cref="Result{T}"/> — an unobtainable required
    /// value — is reported verbatim and produces no Plan either (SPEC §0.6
    /// SG-11).
    /// </para>
    /// </remarks>
    internal sealed class PlanBuilder
    {
        /// <summary>The transition value marking the end of the flow (SPEC §D7 item 2).</summary>
        internal const int EndTransition = -1;

        private readonly RegistrySnapshot _snapshot;
        private readonly IPlanProvenance _provenance;

        internal PlanBuilder(RegistrySnapshot snapshot, IPlanProvenance provenance)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        }

        /// <summary>
        /// Builds the Plan from the context's stage inputs, or reports the
        /// provenance failures and returns null. The caller reaches this stage
        /// only when validation produced no errors.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// A validated document contradicts a recorded stage input — a
        /// pipeline defect, not a document finding.
        /// </exception>
        internal CompiledPlan? Build(ValidationContext context)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // SPEC §0.1 F-05: the configured integrity level is an explicit
            // input at this boundary, not a provider outcome — full is
            // rejected OW-OP-011 before any Plan is produced and never
            // downgraded to weak, whatever the member queries would answer.
            if (_provenance.IntegrityLevel == IntegrityLevel.Full)
            {
                context.Report(ErrorCodes.Create(
                    ErrorCodes.OW_OP_011,
                    "IntegrityLevel=full execution is not supported in this version."));
                return null;
            }

            // Any unobtainable required identifier also rejects the Plan —
            // never a guessed or empty default (SPEC §0.6 SG-11).
            var environment = _provenance.Environment;
            if (!environment.Ok)
            {
                ReportAll(context, environment.Diagnostics);
                return null;
            }

            var document = context.Document;
            var order = context.ExecutionOrder!;
            var stepIndex = StepIndexes(order);
            var outputs = BuildOutputs(context, order, stepIndex);
            if (outputs is null)
            {
                return null;
            }
            var usedTypes = new Dictionary<string, TypeRef>(StringComparer.Ordinal);

            var steps = new List<PlanStep>(order.Count);
            for (var i = 0; i < order.Count; i++)
            {
                var node = order[i];
                var registered = _snapshot.Find(node.Operation, node.Version)
                    ?? throw new InvalidOperationException(
                        $"Validated node '{node.Id}' has no registered operation '{node.Operation}' version {node.Version}.");
                steps.Add(BuildStep(context, node, i, order.Count, registered, stepIndex));
                CollectNodeTypes(node, registered.Descriptor, context, usedTypes);
            }
            CollectInputTypes(document, usedTypes);

            var fingerprints = CollectFingerprints(context, steps, usedTypes);
            if (fingerprints is null)
            {
                return null;
            }

            var hashInput = BuildPlanHashInput(
                CanonicalJson.Version,
                context,
                environment.Value!,
                steps,
                context.Approvals!,
                _snapshot,
                fingerprints.Value);
            var hash = CanonicalJson.Sha256(hashInput.ToToken());

            return new CompiledPlan(
                Identifiers.NewPlanId(),
                hash,
                document.Id,
                document.Canonical.WorkflowHash,
                _snapshot.RegistryHash,
                context.ResolvedInputs ?? JsonValue.From(new JObject()),
                steps,
                outputs,
                UnionEffects(steps),
                BuildApprovals(context.Approvals!, order, stepIndex),
                Array.Empty<PlanSourceMapEntry>(),
                hashInput);
        }

        // SPEC §D7 items 1-3: the step in D5 order with every input port bound
        // to exactly one source, the resolved output types, the {"next": i+1}
        // (or End) transition, the actual D6 effects, and the policy/descriptor
        // timeout-retry-idempotency contract.
        private static PlanStep BuildStep(
            ValidationContext context,
            NodeModel node,
            int index,
            int stepCount,
            RegisteredOperation registered,
            IReadOnlyDictionary<string, int> stepIndex)
        {
            var descriptor = registered.Descriptor;
            var suppliers = context.PortSuppliers![node.Id];
            var inputs = new Dictionary<string, InputSource>(StringComparer.Ordinal);
            foreach (var port in descriptor.Inputs)
            {
                if (!suppliers.TryGetValue(port.Name, out var supplier))
                {
                    // Every port resolves to a supplier on the error-free path:
                    // unfed required ports are OW-VAL-005 and multi-supplied
                    // ports OW-VAL-006 — both errors that stop the build.
                    throw new InvalidOperationException(
                        $"Validated node '{node.Id}' has no supplier recorded for input port '{port.Name}'.");
                }
                inputs.Add(port.Name, ToInputSource(supplier, stepIndex));
            }

            var outputTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in context.OutputTypes![node.Id])
            {
                outputTypes.Add(pair.Key, pair.Value.ToString());
            }

            var transitions = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [V1ControlFlowRules.NextControlPort] =
                    index + 1 < stepCount ? index + 1 : EndTransition,
            };

            var effects = context.Effects is not null &&
                context.Effects.TryGetValue(node.Id, out var actual)
                    ? actual
                    : (IReadOnlyList<string>)Array.Empty<string>();

            var policy = node.Policy;
            return new PlanStep(
                index,
                node.Id,
                descriptor.Id,
                descriptor.Version,
                registered.DescriptorHash,
                registered.ImplementationHash,
                ReadOnlyCollections.CopyMap(inputs),
                ReadOnlyCollections.CopyMap(outputTypes),
                ReadOnlyCollections.CopyMap(transitions),
                ReadOnlyCollections.CopyList(effects),
                policy?.TimeoutSeconds ?? descriptor.DefaultTimeoutSeconds,
                policy?.MaxAttempts is int maxAttempts
                    ? descriptor.DefaultRetry with { MaxAttempts = maxAttempts }
                    : descriptor.DefaultRetry,
                descriptor.Idempotency);
        }

        private static InputSource ToInputSource(
            PortSupplier supplier, IReadOnlyDictionary<string, int> stepIndex) =>
            supplier switch
            {
                PortSupplier.Edge edge => new FromStep(stepIndex[edge.Node], edge.Port),
                PortSupplier.Input input => new FromInput(input.InputId),
                PortSupplier.Parameter parameter => new Constant(parameter.Value),
                PortSupplier.Default @default => new Default(@default.Value),
                PortSupplier.Absent => new Missing(),
                _ => throw new InvalidOperationException(
                    "An unknown port supplier reached the Build Plan stage."),
            };

        private static Dictionary<string, int> StepIndexes(IReadOnlyList<NodeModel> order)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < order.Count; i++)
            {
                // Duplicate node ids are the D2 phase's OW-WF-004; the first
                // declaration wins, matching ValidationContext.NodeOrder.
                map.TryAdd(order[i].Id, i);
            }
            return map;
        }

        // An output referencing a node outside the execution order cannot
        // occur on the error-free path: a dangling reference is OW-VAL-011
        // and an unreachable one OW-VAL-002, and an unknown Operation is the
        // D3 phase's OW-OP-001 — all errors that stop the pipeline before
        // this stage. A from.port the resolved Descriptor does not declare
        // is the same finding the D4 phase reports for a data edge's
        // from.port — OW-OP-005 (SPEC §J), one diagnostic per offending
        // output at its from.port member — and no Plan is produced (SPEC
        // §A6, §D7).
        private IReadOnlyList<PlanOutput>? BuildOutputs(
            ValidationContext context,
            IReadOnlyList<NodeModel> order,
            IReadOnlyDictionary<string, int> stepIndex)
        {
            var nodesById = new Dictionary<string, NodeModel>(StringComparer.Ordinal);
            foreach (var node in order)
            {
                nodesById.TryAdd(node.Id, node);
            }

            var document = context.Document;
            var outputs = new List<PlanOutput>(document.Outputs.Count);
            var failed = false;
            for (var i = 0; i < document.Outputs.Count; i++)
            {
                var output = document.Outputs[i];
                var node = nodesById[output.From.Node];
                var descriptor = _snapshot.Find(node.Operation, node.Version)?.Descriptor
                    ?? throw new InvalidOperationException(
                        $"Validated node '{node.Id}' has no registered operation '{node.Operation}' version {node.Version}.");
                if (!HasPort(descriptor.Outputs, output.From.Port))
                {
                    context.Report(ErrorCodes.Create(
                        ErrorCodes.OW_OP_005,
                        $"The output port '{output.From.Port}' does not exist on operation '{node.Operation}' version {node.Version}.",
                        nodeId: node.Id,
                        port: output.From.Port,
                        jsonPath: $"$.outputs[{i}].from.port"));
                    failed = true;
                    continue;
                }
                outputs.Add(new PlanOutput(output.Id, stepIndex[node.Id], output.From.Port));
            }
            return failed ? null : outputs;
        }

        private static bool HasPort(IReadOnlyList<PortDescriptor> ports, string name)
        {
            for (var i = 0; i < ports.Count; i++)
            {
                if (string.Equals(ports[i].Name, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // The Plan-level effects union in first-seen step order; the plan.json
        // reader re-derives the same list to verify the persisted field.
        internal static IReadOnlyList<string> UnionEffects(IReadOnlyList<PlanStep> steps)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var effects = new List<string>();
            foreach (var step in steps)
            {
                foreach (var effect in step.Effects)
                {
                    if (seen.Add(effect))
                    {
                        effects.Add(effect);
                    }
                }
            }
            return effects;
        }

        private static IReadOnlyList<ApprovalRequirement> BuildApprovals(
            IReadOnlyList<ApprovalRequest> approvals,
            IReadOnlyList<NodeModel> order,
            IReadOnlyDictionary<string, int> stepIndex)
        {
            var nodes = new Dictionary<string, NodeModel>(StringComparer.Ordinal);
            foreach (var node in order)
            {
                nodes.TryAdd(node.Id, node);
            }
            var requirements = new List<ApprovalRequirement>(approvals.Count);
            foreach (var approval in approvals)
            {
                var node = nodes[approval.NodeId];
                requirements.Add(new ApprovalRequirement(
                    approval.NodeId,
                    approval.Effect,
                    $"{node.Operation} v{node.Version}"));
            }
            return requirements;
        }

        // The used TypeRefs: every input/output port type of every used
        // Operation (outputs refined per SPEC §D4 item 6) plus every declared
        // WF input type. Class-id ("type:") leaves are Unity class names, not
        // registered types, and are skipped.
        private static void CollectNodeTypes(
            NodeModel node,
            OperationDescriptor descriptor,
            ValidationContext context,
            IDictionary<string, TypeRef> usedTypes)
        {
            foreach (var port in descriptor.Inputs)
            {
                AddType(port.Type, usedTypes);
            }
            foreach (var pair in context.OutputTypes![node.Id])
            {
                AddTypeRef(pair.Value, usedTypes);
            }
        }

        private static void CollectInputTypes(
            WorkflowDocument document, IDictionary<string, TypeRef> usedTypes)
        {
            foreach (var input in document.Inputs)
            {
                AddType(input.Type, usedTypes);
            }
        }

        private static void AddType(string typeId, IDictionary<string, TypeRef> usedTypes)
        {
            var parsed = TypeRef.Parse(typeId);
            if (parsed.Ok)
            {
                AddTypeRef(parsed.Value!, usedTypes);
            }
        }

        // Adds the TypeRef and its generic argument tree, keyed by the
        // canonical (whitespace-free) form so different spellings deduplicate.
        private static void AddTypeRef(TypeRef type, IDictionary<string, TypeRef> usedTypes)
        {
            if (!type.Name.StartsWith(TypeRef.ClassRefPrefix, StringComparison.Ordinal))
            {
                usedTypes.TryAdd(type.ToString(), type);
            }
            foreach (var arg in type.Args)
            {
                AddTypeRef(arg, usedTypes);
            }
        }

        // Assembles the three fingerprint arrays; null when a provenance
        // query fails (already reported). Each used type must resolve in the
        // snapshot's registry — a validated plan cannot reference otherwise.
        // The operation entries come from the shared PlanHashInputParts
        // helper so the Build Plan stage and plan.json revalidation build the
        // identical member.
        private FingerprintSet? CollectFingerprints(
            ValidationContext context,
            IReadOnlyList<PlanStep> steps,
            IReadOnlyDictionary<string, TypeRef> usedTypes)
        {
            var operationFingerprints = PlanHashInputParts.OperationFingerprints(steps);

            var typeFingerprints = new JArray();
            var codecIdentities = new List<(string CodecId, JObject Identity)>();
            var typeIds = new List<string>(usedTypes.Keys);
            typeIds.Sort(StringComparer.Ordinal);
            foreach (var typeId in typeIds)
            {
                var resolved = _snapshot.Types.Resolve(usedTypes[typeId]);
                if (!resolved.Ok)
                {
                    // Every used type of a validated plan resolves — an
                    // OW-TYPE-001 here means a phase defect, which a Result
                    // pass-through would misname as a document finding.
                    throw new InvalidOperationException(
                        $"Validated plan uses unresolved type '{typeId}'.");
                }
                var definition = resolved.Value!;

                var typeFingerprint = _provenance.TypeFingerprintFor(definition);
                if (!typeFingerprint.Ok)
                {
                    ReportAll(context, typeFingerprint.Diagnostics);
                    return null;
                }
                var typeData = typeFingerprint.Value!;
                typeFingerprints.Add(new JObject
                {
                    ["typeId"] = definition.Id,
                    ["providerId"] = typeData.ProviderId,
                    ["typeDefinitionHash"] = typeData.TypeDefinitionHash,
                    ["definingAssemblyName"] = typeData.DefiningAssemblyName,
                    ["definingAssemblySha256"] = typeData.DefiningAssemblySha256,
                    ["coreApiVersion"] = CoreApi.Version,
                });

                var codecIdentity = _provenance.CodecIdentityFor(definition);
                if (!codecIdentity.Ok)
                {
                    ReportAll(context, codecIdentity.Diagnostics);
                    return null;
                }
                var codecData = codecIdentity.Value!;
                var identity = new JObject
                {
                    ["typeId"] = definition.Id,
                    ["providerId"] = codecData.ProviderId,
                    ["codecTypeFullName"] = codecData.CodecTypeFullName,
                    ["definingAssemblyName"] = codecData.DefiningAssemblyName,
                    ["definingAssemblySha256"] = codecData.DefiningAssemblySha256,
                    ["coreApiVersion"] = CoreApi.Version,
                };
                codecIdentities.Add((CanonicalJson.Sha256(identity), identity));
            }
            codecIdentities.Sort((a, b) => string.CompareOrdinal(a.CodecId, b.CodecId));
            var codecFingerprints = new JArray();
            foreach (var entry in codecIdentities)
            {
                codecFingerprints.Add(entry.Identity);
            }

            return new FingerprintSet(operationFingerprints, typeFingerprints, codecFingerprints);
        }

        internal readonly struct FingerprintSet
        {
            internal FingerprintSet(JArray operations, JArray types, JArray codecs)
            {
                Operations = operations;
                Types = types;
                Codecs = codecs;
            }

            internal JArray Operations { get; }
            internal JArray Types { get; }
            internal JArray Codecs { get; }
        }

        // The SG-12 twelve-field canonical input (SPEC §0.6 SG-12, §D7 item 5).
        // Internal for the golden-vector tests and the plan.json revalidation
        // that recomputes this identical shape (T-W1.6-08). The step-derived
        // members go through the shared PlanHashInputParts helper so the
        // persisted <c>planHashInput</c> member and the reader's rebuild
        // cannot drift.
        internal static JsonValue BuildPlanHashInput(
            int canonicalJsonVersion,
            ValidationContext context,
            PlanEnvironment environment,
            IReadOnlyList<PlanStep> steps,
            IReadOnlyList<ApprovalRequest> approvals,
            RegistrySnapshot snapshot,
            FingerprintSet fingerprints)
        {
            var defines = new JArray();
            foreach (var define in environment.ScriptingDefineSymbols)
            {
                defines.Add(define);
            }

            return JsonValue.From(new JObject
            {
                ["canonicalJsonVersion"] = canonicalJsonVersion,
                ["resolvedWorkflow"] = context.ResolvedWorkflow!.ToToken(),
                ["executionInputs"] = ExecutionInputs(context),
                ["steps"] = PlanHashInputParts.Steps(steps),
                ["transitions"] = PlanHashInputParts.Transitions(steps),
                ["retry"] = PlanHashInputParts.Retry(steps),
                ["timeouts"] = PlanHashInputParts.Timeouts(steps),
                ["requiredEffects"] = PlanHashInputParts.RequiredEffects(
                    steps,
                    BuildApprovals(approvals, context.ExecutionOrder!, StepIndexes(context.ExecutionOrder!))),
                ["usedOperationFingerprints"] = fingerprints.Operations,
                ["usedTypeFingerprints"] = fingerprints.Types,
                ["usedCodecFingerprints"] = fingerprints.Codecs,
                ["environmentIdentifiers"] = new JObject
                {
                    ["unityVersion"] = environment.UnityVersion,
                    ["editorOS"] = environment.EditorOS,
                    ["activeBuildTarget"] = environment.ActiveBuildTarget,
                    ["scriptingBackend"] = environment.ScriptingBackend,
                    ["apiCompatibilityLevel"] = environment.ApiCompatibilityLevel,
                    ["scriptingDefineSymbols"] = defines,
                },
            });
        }

        // Every declared WF input keyed to an unambiguous tag through the
        // shared PlanHashInputParts builder, so the persisted
        // <c>planHashInput</c> member and the plan.json reader's rebuild —
        // which takes the declared ids from the persisted resolvedWorkflow —
        // cannot drift (SPEC §0.6 SG-12, the 2026-11-06 amendment).
        private static JObject ExecutionInputs(ValidationContext context)
        {
            var declared = new List<string>(context.Document.Inputs.Count);
            foreach (var input in context.Document.Inputs)
            {
                declared.Add(input.Id);
            }
            return PlanHashInputParts.ExecutionInputs(
                declared, context.ResolvedInputs?.ToToken());
        }

        private static void ReportAll(ValidationContext context, IReadOnlyList<Diagnostic> diagnostics)
        {
            for (var i = 0; i < diagnostics.Count; i++)
            {
                context.Report(diagnostics[i]);
            }
        }
    }
}
