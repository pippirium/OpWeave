#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The canonical persistence codec of <see cref="CompiledPlan"/>
    /// (SPEC §D7 items 5-6, §D0.1, §0.6 SG-12 as amended 2026-11-06):
    /// <see cref="Write"/> emits the plan.json Canonical JSON text — its
    /// UTF-8 no-BOM encoding is the byte sequence the Run Store persists —
    /// and <see cref="Read"/> reloads it with strict version checks and a
    /// planHash recomputation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document carries every <see cref="CompiledPlan"/> public field
    /// camelCased — <c>formatVersion</c>, <c>canonicalJsonVersion</c>,
    /// <c>planId</c>, <c>planHash</c>, <c>workflowId</c>, <c>workflowHash</c>,
    /// <c>registryHash</c>, <c>inputs</c>, <c>steps</c>, <c>outputs</c>,
    /// <c>effects</c>, <c>approvals</c> and the reserved <c>sourceMap</c>
    /// (SPEC §N.5.5 item 2) — plus <c>planHashInput</c>, the verbatim SG-12
    /// twelve-field hash input the compiler produced (SPEC §0.6 SG-12; the
    /// member is required by the 2026-11-06 amendment). The
    /// extra member is the required revalidation surface: SPEC §D0.1 orders
    /// planHash recomputation on load before the current-fingerprint
    /// comparison, and <c>resolvedWorkflow</c>, <c>executionInputs</c>, the
    /// Type/Codec fingerprints and the environment identifiers are not part
    /// of the Plan's public surface — without the persisted input the hash
    /// could not be recomputed at all. The member set is closed: an unknown
    /// semantic field or a missing/mistyped/unknown
    /// <c>formatVersion</c>/<c>canonicalJsonVersion</c> is rejected, and
    /// plan.json never contains its own digest — <c>planFileDigest</c> lives
    /// only in the RunAccepted journal record (SPEC §D0.1, §F2).
    /// </para>
    /// <para>
    /// <see cref="Read"/> verifies three layers before yielding a Plan: the
    /// version gate (missing, mistyped or unknown versions are
    /// <see cref="PlanJsonStatus.NeedsReview"/> and never interpreted), the
    /// strict member decode (a member outside the v1 set or a missing,
    /// mistyped or malformed member is <see cref="PlanJsonStatus.Corrupt"/>),
    /// and the revalidation the 2026-11-06 SG-12 amendment orders —
    /// <c>planHash</c> equality against the SHA-256 of the persisted
    /// <c>planHashInput</c> plus consistency between the persisted public
    /// fields and the persisted hash input, whose absence or failure is
    /// <see cref="PlanJsonStatus.NeedsReview"/>. The derivable members
    /// (<c>executionInputs</c>, <c>steps</c>, <c>transitions</c>,
    /// <c>retry</c>, <c>timeouts</c>, <c>requiredEffects</c>,
    /// <c>usedOperationFingerprints</c>) are rebuilt from the decoded Plan —
    /// <c>executionInputs</c> additionally needing the declared input ids
    /// carried by the persisted <c>resolvedWorkflow</c> — through
    /// <see cref="PlanHashInputParts"/> and compared byte-exactly, while
    /// <c>outputs</c>↔<c>resolvedWorkflow.outputs</c>,
    /// <c>workflowId</c>↔<c>resolvedWorkflow.id</c>, the nested↔public
    /// <c>canonicalJsonVersion</c> and <c>effects</c>↔the step-effect union
    /// are cross-checked so an edit to any semantic member is rejected. Fields the SPEC deliberately keeps
    /// out of planHash (<c>planId</c>, <c>workflowHash</c>,
    /// <c>registryHash</c>, the reserved <c>sourceMap</c>) carry no execution
    /// meaning; their byte-level tampering is the planFileDigest check the
    /// Run Store performs (SPEC §D0.1). Failure diagnostics follow the SG-02
    /// internal convention — an empty <see cref="Diagnostic.Code"/>, a
    /// payload-free fixed-English message and a JsonPath — for the owning
    /// layer to map to OW-STORE-002 exactly once.
    /// </para>
    /// </remarks>
    public static class PlanJsonCodec
    {
        /// <summary>The <c>formatVersion</c> this codec writes and accepts: 1 (SPEC §D7, §N.1).</summary>
        public const int FormatVersion = 1;

        private const string FormatVersionMember = "formatVersion";
        private const string CanonicalJsonVersionMember = "canonicalJsonVersion";
        private const string PlanIdMember = "planId";
        private const string PlanHashMember = "planHash";
        private const string WorkflowIdMember = "workflowId";
        private const string WorkflowHashMember = "workflowHash";
        private const string RegistryHashMember = "registryHash";
        private const string InputsMember = "inputs";
        private const string StepsMember = "steps";
        private const string OutputsMember = "outputs";
        private const string EffectsMember = "effects";
        private const string ApprovalsMember = "approvals";
        private const string SourceMapMember = "sourceMap";
        private const string PlanHashInputMember = "planHashInput";

        private const string IndexMember = "index";
        private const string NodeIdMember = "nodeId";
        private const string OperationIdMember = "operationId";
        private const string VersionMember = "version";
        private const string DescriptorHashMember = "descriptorHash";
        private const string ImplementationHashMember = "implementationHash";
        private const string OutputTypesMember = "outputTypes";
        private const string TransitionsMember = "transitions";
        private const string TimeoutSecondsMember = "timeoutSeconds";
        private const string TimeoutsMember = "timeouts";
        private const string RetryMember = "retry";
        private const string IdempotencyMember = "idempotency";
        private const string MaxAttemptsMember = "maxAttempts";
        private const string InitialBackoffMsMember = "initialBackoffMs";
        private const string MultiplierMember = "multiplier";
        private const string MaxBackoffMsMember = "maxBackoffMs";
        private const string IdMember = "id";
        private const string StepIndexMember = "stepIndex";
        private const string PortMember = "port";
        private const string EffectMember = "effect";
        private const string DetailMember = "detail";
        private const string SourcePathMember = "sourcePath";
        private const string TypeMember = "type";
        private const string RequiredMember = "required";
        private const string DefaultMember = "default";

        private const string ResolvedWorkflowMember = "resolvedWorkflow";
        private const string ExecutionInputsMember = "executionInputs";
        private const string RequiredEffectsMember = "requiredEffects";
        private const string UsedOperationFingerprintsMember = "usedOperationFingerprints";
        private const string UsedTypeFingerprintsMember = "usedTypeFingerprints";
        private const string UsedCodecFingerprintsMember = "usedCodecFingerprints";
        private const string EnvironmentIdentifiersMember = "environmentIdentifiers";
        private const string FromMember = "from";
        private const string NodeMember = "node";
        private const string UnityVersionMember = "unityVersion";
        private const string EditorOSMember = "editorOS";
        private const string ActiveBuildTargetMember = "activeBuildTarget";
        private const string ScriptingBackendMember = "scriptingBackend";
        private const string ApiCompatibilityLevelMember = "apiCompatibilityLevel";
        private const string ScriptingDefineSymbolsMember = "scriptingDefineSymbols";

        private static readonly HashSet<string> s_topMembers = new HashSet<string>(
            StringComparer.Ordinal)
        {
            FormatVersionMember, CanonicalJsonVersionMember, PlanIdMember, PlanHashMember,
            WorkflowIdMember, WorkflowHashMember, RegistryHashMember, InputsMember,
            StepsMember, OutputsMember, EffectsMember, ApprovalsMember, SourceMapMember,
            PlanHashInputMember,
        };

        private static readonly HashSet<string> s_hashInputMembers = new HashSet<string>(
            StringComparer.Ordinal)
        {
            CanonicalJsonVersionMember, ResolvedWorkflowMember, ExecutionInputsMember,
            StepsMember, TransitionsMember, RetryMember, TimeoutsMember,
            RequiredEffectsMember, UsedOperationFingerprintsMember,
            UsedTypeFingerprintsMember, UsedCodecFingerprintsMember,
            EnvironmentIdentifiersMember,
        };

        private static readonly HashSet<string> s_stepMembers = new HashSet<string>(
            StringComparer.Ordinal)
        {
            IndexMember, NodeIdMember, OperationIdMember, VersionMember,
            DescriptorHashMember, ImplementationHashMember, InputsMember,
            OutputTypesMember, TransitionsMember, EffectsMember,
            TimeoutSecondsMember, RetryMember, IdempotencyMember,
        };

        private static readonly HashSet<string> s_retryMembers = new HashSet<string>(
            StringComparer.Ordinal)
        {
            MaxAttemptsMember, InitialBackoffMsMember, MultiplierMember, MaxBackoffMsMember,
        };

        private static readonly HashSet<string> s_outputMembers = new HashSet<string>(
            StringComparer.Ordinal) { IdMember, StepIndexMember, PortMember };

        private static readonly HashSet<string> s_declaredInputMembers = new HashSet<string>(
            StringComparer.Ordinal) { IdMember, TypeMember, RequiredMember, DefaultMember };

        private static readonly HashSet<string> s_approvalMembers = new HashSet<string>(
            StringComparer.Ordinal) { NodeIdMember, EffectMember, DetailMember };

        private static readonly HashSet<string> s_sourceMapMembers = new HashSet<string>(
            StringComparer.Ordinal) { StepIndexMember, SourcePathMember };

        private static readonly HashSet<string> s_environmentMembers = new HashSet<string>(
            StringComparer.Ordinal)
        {
            UnityVersionMember, EditorOSMember, ActiveBuildTargetMember,
            ScriptingBackendMember, ApiCompatibilityLevelMember,
            ScriptingDefineSymbolsMember,
        };

        private static readonly HashSet<string> s_typeFingerprintMembers = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "typeId", "providerId", "typeDefinitionHash", "definingAssemblyName",
            "definingAssemblySha256", "coreApiVersion",
        };

        private static readonly HashSet<string> s_codecFingerprintMembers = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "typeId", "providerId", "codecTypeFullName", "definingAssemblyName",
            "definingAssemblySha256", "coreApiVersion",
        };

        /// <summary>
        /// Serializes <paramref name="plan"/> as the plan.json Canonical JSON
        /// text (SPEC §D7 item 6, §0.6 SG-12). Its UTF-8 no-BOM encoding is
        /// the persisted byte sequence whose SHA-256 becomes the RunAccepted
        /// <c>planFileDigest</c> — the digest itself is never a member.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="plan"/> is outside the persisted contract: an empty
        /// identity member, a hash member that is not 64 lowercase hex
        /// digits, a negative index/version/timeout, an out-of-range
        /// transition, a null required member or a <see cref="CompiledPlan.PlanHashInput"/>
        /// that is not the SG-12 twelve-member object.
        /// </exception>
        public static string Write(CompiledPlan plan)
        {
            if (plan is null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            RequireIdentifier(plan.PlanId, "planId");
            RequireLowerHex64(plan.PlanHash, "planHash");
            RequireNonEmpty(plan.WorkflowId, "workflowId");
            RequireLowerHex64(plan.WorkflowHash, "workflowHash");
            RequireLowerHex64(plan.RegistryHash, "registryHash");
            if (plan.PlanHashInput.ToToken() is not JObject hashInput ||
                !HasOnly(hashInput, s_hashInputMembers))
            {
                throw new ArgumentException(
                    "CompiledPlan.PlanHashInput must be the SG-12 twelve-member object.",
                    nameof(plan));
            }

            var steps = new JArray();
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                steps.Add(WriteStep(plan.Steps[i], i, plan.Steps.Count));
            }
            var outputs = new JArray();
            foreach (var output in plan.Outputs)
            {
                RequireNonEmpty(output.Id, "outputs.id");
                RequireNonEmpty(output.Port, "outputs.port");
                RequireStepIndex(output.StepIndex, plan.Steps.Count, "outputs.stepIndex");
                outputs.Add(new JObject
                {
                    [IdMember] = output.Id,
                    [StepIndexMember] = output.StepIndex,
                    [PortMember] = output.Port,
                });
            }
            var effects = new JArray();
            foreach (var effect in plan.Effects)
            {
                RequireNonEmpty(effect, "effects[]");
                effects.Add(effect);
            }
            var approvals = new JArray();
            foreach (var approval in plan.Approvals)
            {
                RequireNonEmpty(approval.NodeId, "approvals.nodeId");
                RequireNonEmpty(approval.Effect, "approvals.effect");
                RequireNonEmpty(approval.Detail, "approvals.detail");
                approvals.Add(new JObject
                {
                    [NodeIdMember] = approval.NodeId,
                    [EffectMember] = approval.Effect,
                    [DetailMember] = approval.Detail,
                });
            }
            var sourceMap = new JArray();
            foreach (var entry in plan.SourceMap)
            {
                RequireStepIndex(entry.StepIndex, plan.Steps.Count, "sourceMap.stepIndex");
                RequireNonEmpty(entry.SourcePath, "sourceMap.sourcePath");
                sourceMap.Add(new JObject
                {
                    [StepIndexMember] = entry.StepIndex,
                    [SourcePathMember] = entry.SourcePath,
                });
            }

            return CanonicalJson.Serialize(new JObject
            {
                [FormatVersionMember] = FormatVersion,
                [CanonicalJsonVersionMember] = CanonicalJson.Version,
                [PlanIdMember] = plan.PlanId,
                [PlanHashMember] = plan.PlanHash,
                [WorkflowIdMember] = plan.WorkflowId,
                [WorkflowHashMember] = plan.WorkflowHash,
                [RegistryHashMember] = plan.RegistryHash,
                [InputsMember] = plan.Inputs.ToToken(),
                [StepsMember] = steps,
                [OutputsMember] = outputs,
                [EffectsMember] = effects,
                [ApprovalsMember] = approvals,
                [SourceMapMember] = sourceMap,
                [PlanHashInputMember] = hashInput,
            });
        }

        /// <summary>
        /// Reloads a plan.json document (SPEC §D0.1, §D7 item 6, §0.6 SG-12
        /// as amended 2026-11-06): the version gate, the strict member
        /// decode, the <c>planHash</c>/<c>planHashInput</c> recomputation and
        /// the field-consistency cross-checks described on the type. A
        /// rejected document is classified
        /// <see cref="PlanJsonStatus.NeedsReview"/> (version problems, or a
        /// <c>planHashInput</c> that is missing, whose SHA-256 disagrees with
        /// <c>planHash</c>, or that contradicts the public fields — never
        /// interpreted or auto-succeeded) or
        /// <see cref="PlanJsonStatus.Corrupt"/> (a format violation); the
        /// Run Store maps either to OW-STORE-002 and NeedsReview once.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
        public static PlanJsonRead Read(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }
            var parsed = CanonicalJson.Parse(text);
            if (!parsed.Ok)
            {
                return Corrupt(parsed.Diagnostics[0]);
            }
            if (parsed.Value is not JObject root)
            {
                return Corrupt(Error("A plan.json document must be a JSON object.", "$"));
            }

            // The version gate comes first: a document naming a version this
            // reader does not know is never interpreted (SPEC §F2 item 4).
            if (!IsVersion(root[FormatVersionMember], FormatVersion) ||
                !IsVersion(root[CanonicalJsonVersionMember], CanonicalJson.Version))
            {
                return Review(Error(
                    "A plan.json version is unknown, missing or mistyped.", "$"));
            }
            if (!HasOnly(root, s_topMembers))
            {
                return Corrupt(Error(
                    "A plan.json document has a member outside the v1 set.", "$"));
            }

            var planId = StringMember(root[PlanIdMember]);
            if (planId is null ||
                !planId.StartsWith(Identifiers.PlanIdPrefix, StringComparison.Ordinal))
            {
                return Corrupt(Error(
                    "A plan.json planId is missing, mistyped or not a Plan identifier.", "$.*"));
            }
            var planHash = StringMember(root[PlanHashMember]);
            if (!IsLowerHex64(planHash))
            {
                return Corrupt(Error(
                    "A plan.json planHash is missing, mistyped or not a SHA-256 hex.", "$.*"));
            }
            var workflowId = StringMember(root[WorkflowIdMember]);
            if (workflowId is null || workflowId.Length == 0)
            {
                return Corrupt(Error(
                    "A plan.json workflowId is missing, empty or not a JSON string.", "$.*"));
            }
            if (!IsLowerHex64(StringMember(root[WorkflowHashMember])) ||
                !IsLowerHex64(StringMember(root[RegistryHashMember])))
            {
                return Corrupt(Error(
                    "A plan.json hash member is missing, mistyped or not a SHA-256 hex.", "$.*"));
            }
            var inputsToken = root[InputsMember];
            if (inputsToken is null)
            {
                return Corrupt(Error("A plan.json inputs member is missing.", "$"));
            }
            var inputs = JsonValue.From(inputsToken);

            if (root[StepsMember] is not JArray stepsToken)
            {
                return Corrupt(Error(
                    "A plan.json steps member is missing or not a JSON array.", "$.*"));
            }
            var steps = new List<PlanStep>(stepsToken.Count);
            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < stepsToken.Count; i++)
            {
                var failure = DecodeStep(
                    stepsToken[i], i, stepsToken.Count, nodeIds, out var step);
                if (failure is not null)
                {
                    return failure;
                }
                steps.Add(step!);
            }

            if (root[OutputsMember] is not JArray outputsToken)
            {
                return Corrupt(Error(
                    "A plan.json outputs member is missing or not a JSON array.", "$.*"));
            }
            var outputs = new List<PlanOutput>(outputsToken.Count);
            var outputIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in outputsToken)
            {
                if (token is not JObject entry || !HasOnly(entry, s_outputMembers))
                {
                    return Corrupt(Error(
                        "A plan.json output entry is not the v1 three-member object.", "$.*"));
                }
                var id = StringMember(entry[IdMember]);
                var port = StringMember(entry[PortMember]);
                if (id is null || id.Length == 0 || port is null || port.Length == 0 ||
                    !IntegerMember(entry[StepIndexMember], 0, steps.Count - 1, out var stepIndex) ||
                    !outputIds.Add(id))
                {
                    return Corrupt(Error(
                        "A plan.json output entry has a missing, mistyped or out-of-range member.",
                        "$.*"));
                }
                outputs.Add(new PlanOutput(id, stepIndex, port));
            }

            if (root[EffectsMember] is not JArray effectsToken)
            {
                return Corrupt(Error(
                    "A plan.json effects member is missing or not a JSON array.", "$.*"));
            }
            var effects = new List<string>(effectsToken.Count);
            foreach (var token in effectsToken)
            {
                var effect = StringMember(token);
                if (effect is null || effect.Length == 0)
                {
                    return Corrupt(Error(
                        "A plan.json effects member holds a non-string or empty value.", "$.*"));
                }
                effects.Add(effect);
            }

            if (root[ApprovalsMember] is not JArray approvalsToken)
            {
                return Corrupt(Error(
                    "A plan.json approvals member is missing or not a JSON array.", "$.*"));
            }
            var approvals = new List<ApprovalRequirement>(approvalsToken.Count);
            foreach (var token in approvalsToken)
            {
                if (token is not JObject entry || !HasOnly(entry, s_approvalMembers))
                {
                    return Corrupt(Error(
                        "A plan.json approval entry is not the v1 three-member object.", "$.*"));
                }
                var nodeId = StringMember(entry[NodeIdMember]);
                var effect = StringMember(entry[EffectMember]);
                var detail = StringMember(entry[DetailMember]);
                if (nodeId is null || nodeId.Length == 0 || effect is null ||
                    effect.Length == 0 || detail is null || detail.Length == 0)
                {
                    return Corrupt(Error(
                        "A plan.json approval entry has a missing, empty or mistyped member.",
                        "$.*"));
                }
                approvals.Add(new ApprovalRequirement(nodeId, effect, detail));
            }

            if (root[SourceMapMember] is not JArray sourceMapToken)
            {
                return Corrupt(Error(
                    "A plan.json sourceMap member is missing or not a JSON array.", "$.*"));
            }
            var sourceMap = new List<PlanSourceMapEntry>(sourceMapToken.Count);
            foreach (var token in sourceMapToken)
            {
                if (token is not JObject entry || !HasOnly(entry, s_sourceMapMembers))
                {
                    return Corrupt(Error(
                        "A plan.json sourceMap entry is not the v1 two-member object.", "$.*"));
                }
                var sourcePath = StringMember(entry[SourcePathMember]);
                if (sourcePath is null || sourcePath.Length == 0 ||
                    !IntegerMember(entry[StepIndexMember], 0, steps.Count - 1, out var stepIndex))
                {
                    return Corrupt(Error(
                        "A plan.json sourceMap entry has a missing, mistyped or out-of-range member.",
                        "$.*"));
                }
                sourceMap.Add(new PlanSourceMapEntry(stepIndex, sourcePath));
            }

            // The persisted hash input: the revalidation surface for the
            // SPEC §D0.1 planHash recomputation, required by the 2026-11-06
            // SG-12 amendment. A missing member, a hash mismatch or a
            // contradiction with the public fields is NeedsReview — the
            // document reads, but the Plan it claims to be cannot be
            // verified. It must be exactly the SG-12 twelve members (an
            // extra member is a format violation), its canonicalJsonVersion
            // must agree with the document's, and its SHA-256 must equal the
            // stored planHash.
            if (root[PlanHashInputMember] is not JObject hashInput)
            {
                return Review(Error(
                    "A plan.json planHashInput member is missing or not a JSON object.", "$.*"));
            }
            if (!HasOnly(hashInput, s_hashInputMembers))
            {
                return Corrupt(Error(
                    "A plan.json planHashInput has a member outside the SG-12 twelve.", "$.*"));
            }
            if (!IsVersion(hashInput[CanonicalJsonVersionMember], CanonicalJson.Version))
            {
                return Review(Error(
                    "A plan.json planHashInput canonicalJsonVersion differs from the document's.",
                    "$.*"));
            }
            var shapeFailure = CheckHashInputShapes(hashInput, workflowId, inputs, steps, outputs);
            if (shapeFailure is not null)
            {
                return shapeFailure;
            }
            if (!string.Equals(
                planHash, CanonicalJson.Sha256(hashInput), StringComparison.Ordinal))
            {
                return Review(Error(
                    "A plan.json planHash does not match the recomputed planHashInput hash.",
                    "$"));
            }
            var consistencyFailure = CheckHashInputConsistency(
                hashInput, steps, approvals, effects);
            if (consistencyFailure is not null)
            {
                return consistencyFailure;
            }

            return new PlanJsonRead(
                PlanJsonStatus.Decoded,
                new CompiledPlan(
                    planId, planHash!, workflowId, workflowHash: StringMember(root[WorkflowHashMember])!,
                    registryHash: StringMember(root[RegistryHashMember])!, inputs, steps, outputs,
                    effects, approvals, sourceMap, JsonValue.From(hashInput)),
                null);
        }

        // The persisted step object: every PlanStep member camelCased. Input
        // sources decode through the shared SG-12 tag reader; the stepIndex a
        // FromStep names must precede the step (SPEC §D5 item 5) and a
        // transition target is End (-1) or an existing step (SPEC §D7 item 2).
        private static JObject WriteStep(PlanStep step, int position, int stepCount)
        {
            if (step.Index != position)
            {
                throw new ArgumentException(
                    "PlanStep.Index must equal its position in the Plan's step order.", "plan");
            }
            RequireNonEmpty(step.NodeId, "steps.nodeId");
            RequireNonEmpty(step.OperationId, "steps.operationId");
            if (step.Version < 0)
            {
                throw new ArgumentException("PlanStep.Version must not be negative.", "plan");
            }
            RequireLowerHex64(step.DescriptorHash, "steps.descriptorHash");
            RequireLowerHex64(step.ImplementationHash, "steps.implementationHash");
            if (step.TimeoutSeconds < 0)
            {
                throw new ArgumentException("PlanStep.TimeoutSeconds must not be negative.", "plan");
            }

            var inputs = new JObject();
            foreach (var pair in step.Inputs)
            {
                RequireNonEmpty(pair.Key, "steps.inputs port");
                inputs[pair.Key] = InputSourceJson.Write(pair.Value).ToToken();
            }
            var outputTypes = new JObject();
            foreach (var pair in step.OutputTypes)
            {
                RequireNonEmpty(pair.Key, "steps.outputTypes port");
                RequireNonEmpty(pair.Value, "steps.outputTypes type");
                outputTypes[pair.Key] = pair.Value;
            }
            var transitions = new JObject();
            foreach (var pair in step.Transitions)
            {
                RequireNonEmpty(pair.Key, "steps.transitions port");
                if (pair.Value < -1 || pair.Value >= stepCount)
                {
                    throw new ArgumentException(
                        "A PlanStep transition must be End (-1) or an existing step index.",
                        "plan");
                }
                transitions[pair.Key] = pair.Value;
            }
            var effects = new JArray();
            foreach (var effect in step.Effects)
            {
                RequireNonEmpty(effect, "steps.effects[]");
                effects.Add(effect);
            }
            var retry = new JObject
            {
                [MaxAttemptsMember] = step.Retry.MaxAttempts,
                [InitialBackoffMsMember] = step.Retry.InitialBackoffMs,
                [MultiplierMember] = step.Retry.Multiplier,
                [MaxBackoffMsMember] = step.Retry.MaxBackoffMs,
            };

            return new JObject
            {
                [IndexMember] = step.Index,
                [NodeIdMember] = step.NodeId,
                [OperationIdMember] = step.OperationId,
                [VersionMember] = step.Version,
                [DescriptorHashMember] = step.DescriptorHash,
                [ImplementationHashMember] = step.ImplementationHash,
                [InputsMember] = inputs,
                [OutputTypesMember] = outputTypes,
                [TransitionsMember] = transitions,
                [EffectsMember] = effects,
                [TimeoutSecondsMember] = step.TimeoutSeconds,
                [RetryMember] = retry,
                [IdempotencyMember] = JsonNaming.ToSnakeCase(step.Idempotency.ToString()),
            };
        }

        // Decodes one step object; returns the failure read or null on
        // success with <paramref name="step"/> populated.
        private static PlanJsonRead? DecodeStep(
            JToken token, int position, int stepCount,
            HashSet<string> nodeIds, out PlanStep? step)
        {
            step = null;
            if (token is not JObject entry || !HasOnly(entry, s_stepMembers))
            {
                return Corrupt(Error(
                    "A plan.json step is not the v1 thirteen-member object.", "$.*"));
            }
            if (!IntegerMember(entry[IndexMember], 0, int.MaxValue, out var index) ||
                index != position)
            {
                return Corrupt(Error(
                    "A plan.json step index is missing, mistyped or out of sequence.", "$.*"));
            }
            var nodeId = StringMember(entry[NodeIdMember]);
            var operationId = StringMember(entry[OperationIdMember]);
            if (nodeId is null || nodeId.Length == 0 ||
                operationId is null || operationId.Length == 0)
            {
                return Corrupt(Error(
                    "A plan.json step nodeId or operationId is missing, empty or not a string.",
                    "$.*"));
            }
            if (!nodeIds.Add(nodeId))
            {
                return Corrupt(Error("A plan.json step repeats a nodeId.", "$.*"));
            }
            if (!IntegerMember(entry[VersionMember], 0, int.MaxValue, out var version))
            {
                return Corrupt(Error(
                    "A plan.json step version is missing, mistyped or negative.", "$.*"));
            }
            var descriptorHash = StringMember(entry[DescriptorHashMember]);
            var implementationHash = StringMember(entry[ImplementationHashMember]);
            if (!IsLowerHex64(descriptorHash) || !IsLowerHex64(implementationHash))
            {
                return Corrupt(Error(
                    "A plan.json step fingerprint is missing, mistyped or not a SHA-256 hex.",
                    "$.*"));
            }
            if (entry[InputsMember] is not JObject inputsToken)
            {
                return Corrupt(Error(
                    "A plan.json step inputs member is missing or not a JSON object.", "$.*"));
            }
            var inputs = new Dictionary<string, InputSource>(StringComparer.Ordinal);
            foreach (var property in inputsToken.Properties())
            {
                var decoded = InputSourceJson.Read(JsonValue.From(property.Value));
                if (!decoded.Ok)
                {
                    return Corrupt(decoded.Diagnostics[0]);
                }
                if (decoded.Value is FromStep fromStep && fromStep.StepIndex >= position)
                {
                    return Corrupt(Error(
                        "A plan.json step input comes from a step that is not earlier.", "$.*"));
                }
                inputs.Add(property.Name, decoded.Value!);
            }
            if (entry[OutputTypesMember] is not JObject outputTypesToken)
            {
                return Corrupt(Error(
                    "A plan.json step outputTypes member is missing or not a JSON object.", "$.*"));
            }
            var outputTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in outputTypesToken.Properties())
            {
                var type = StringMember(property.Value);
                if (type is null || type.Length == 0)
                {
                    return Corrupt(Error(
                        "A plan.json step outputTypes member has a non-string or empty value.",
                        "$.*"));
                }
                outputTypes.Add(property.Name, type);
            }
            if (entry[TransitionsMember] is not JObject transitionsToken)
            {
                return Corrupt(Error(
                    "A plan.json step transitions member is missing or not a JSON object.", "$.*"));
            }
            var transitions = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in transitionsToken.Properties())
            {
                if (property.Name.Length == 0 ||
                    !IntegerMember(property.Value, -1, stepCount - 1, out var target))
                {
                    return Corrupt(Error(
                        "A plan.json step transition is mistyped or out of range.", "$.*"));
                }
                transitions.Add(property.Name, target);
            }
            if (entry[EffectsMember] is not JArray effectsToken)
            {
                return Corrupt(Error(
                    "A plan.json step effects member is missing or not a JSON array.", "$.*"));
            }
            var effects = new List<string>(effectsToken.Count);
            foreach (var effectToken in effectsToken)
            {
                var effect = StringMember(effectToken);
                if (effect is null || effect.Length == 0)
                {
                    return Corrupt(Error(
                        "A plan.json step effects member holds a non-string or empty value.",
                        "$.*"));
                }
                effects.Add(effect);
            }
            if (!IntegerMember(entry[TimeoutSecondsMember], 0, int.MaxValue, out var timeout))
            {
                return Corrupt(Error(
                    "A plan.json step timeoutSeconds is missing, mistyped or negative.", "$.*"));
            }
            if (entry[RetryMember] is not JObject retryToken || !HasOnly(retryToken, s_retryMembers))
            {
                return Corrupt(Error(
                    "A plan.json step retry member is not the v1 four-member object.", "$.*"));
            }
            if (!IntegerMember(retryToken[MaxAttemptsMember], 1, int.MaxValue, out var maxAttempts) ||
                !IntegerMember(retryToken[InitialBackoffMsMember], 0, int.MaxValue, out var initialBackoff) ||
                !NumberMember(retryToken[MultiplierMember], out var multiplier) ||
                multiplier <= 0.0 ||
                !IntegerMember(retryToken[MaxBackoffMsMember], 0, int.MaxValue, out var maxBackoff))
            {
                return Corrupt(Error(
                    "A plan.json step retry member has a missing, mistyped or out-of-range field.",
                    "$.*"));
            }
            var idempotency = StringMember(entry[IdempotencyMember]) switch
            {
                "idempotent" => (Idempotency?)Idempotency.Idempotent,
                "reconcilable" => (Idempotency?)Idempotency.Reconcilable,
                "unsafe" => (Idempotency?)Idempotency.Unsafe,
                _ => null,
            };
            if (idempotency is null)
            {
                return Corrupt(Error(
                    "A plan.json step idempotency is missing or not a v1 snake_case name.", "$.*"));
            }

            step = new PlanStep(
                index, nodeId, operationId, version, descriptorHash!, implementationHash!,
                ReadOnlyCollections.CopyMap(inputs),
                ReadOnlyCollections.CopyMap(outputTypes),
                ReadOnlyCollections.CopyMap(transitions),
                ReadOnlyCollections.CopyList(effects),
                timeout,
                new RetryPolicy(maxAttempts, initialBackoff, multiplier, maxBackoff),
                idempotency.Value);
            return null;
        }

        // The checks that need the persisted resolvedWorkflow (2026-11-06
        // SG-12 amendment items 2, 3 and 6): the public workflowId must equal
        // its id, each public output must correspond to one of its outputs by
        // id — the order is not compared and the public order is kept — and
        // the persisted executionInputs member must equal the deterministic
        // Build Plan rebuild over its declared inputs and the public inputs,
        // through the shared PlanHashInputParts helper. The Type/Codec
        // fingerprint entries and the six environment keys are not derivable,
        // so only their v1 shapes are checked. A malformed member is a format
        // violation (Corrupt); a member that contradicts the public fields
        // fails the revalidation (NeedsReview).
        private static PlanJsonRead? CheckHashInputShapes(
            JObject hashInput, string workflowId, JsonValue inputs,
            IReadOnlyList<PlanStep> steps, IReadOnlyList<PlanOutput> outputs)
        {
            if (hashInput[ResolvedWorkflowMember] is not JObject resolvedWorkflow)
            {
                return Corrupt(Error(
                    "A plan.json planHashInput resolvedWorkflow is missing or not a JSON object.",
                    "$.*"));
            }
            var resolvedId = StringMember(resolvedWorkflow[IdMember]);
            if (resolvedId is null || resolvedId.Length == 0)
            {
                return Corrupt(Error(
                    "A plan.json planHashInput resolvedWorkflow id is missing, empty or not a string.",
                    "$.*"));
            }
            if (!string.Equals(resolvedId, workflowId, StringComparison.Ordinal))
            {
                return Review(Error(
                    "A plan.json workflowId disagrees with the persisted resolvedWorkflow.",
                    "$.*"));
            }
            if (resolvedWorkflow[OutputsMember] is not JArray resolvedOutputs)
            {
                return Corrupt(Error(
                    "A plan.json planHashInput resolvedWorkflow outputs is missing or not a JSON array.",
                    "$.*"));
            }
            var resolvedById = new Dictionary<string, (string Node, string Port)>(
                StringComparer.Ordinal);
            foreach (var token in resolvedOutputs)
            {
                if (token is not JObject entry ||
                    !HasOnly(entry, IdMember, FromMember) ||
                    entry[FromMember] is not JObject from ||
                    !HasOnly(from, NodeMember, PortMember))
                {
                    return Corrupt(Error(
                        "A plan.json planHashInput resolvedWorkflow output is not the v1 form.",
                        "$.*"));
                }
                var id = StringMember(entry[IdMember]);
                var node = StringMember(from[NodeMember]);
                var port = StringMember(from[PortMember]);
                if (id is null || id.Length == 0 || node is null || node.Length == 0 ||
                    port is null || port.Length == 0 || !resolvedById.TryAdd(id, (node, port)))
                {
                    return Corrupt(Error(
                        "A plan.json planHashInput resolvedWorkflow output has a missing, empty or duplicated member.",
                        "$.*"));
                }
            }
            if (resolvedById.Count != outputs.Count)
            {
                return Review(Error(
                    "A plan.json outputs member disagrees with the persisted resolvedWorkflow.",
                    "$.*"));
            }
            foreach (var output in outputs)
            {
                if (!resolvedById.TryGetValue(output.Id, out var resolved) ||
                    !string.Equals(resolved.Node, steps[output.StepIndex].NodeId, StringComparison.Ordinal) ||
                    !string.Equals(resolved.Port, output.Port, StringComparison.Ordinal))
                {
                    return Review(Error(
                        "A plan.json output entry disagrees with the persisted resolvedWorkflow.",
                        "$.*"));
                }
            }

            // The declared inputs resolvedWorkflow carries (the canonical
            // form's {id,type,required,default?} entries): they drive the
            // deterministic executionInputs rebuild — a declared input the
            // public inputs omit is a missing tag and a supplied member
            // naming an undeclared input is excluded, so the byte-exact
            // comparison covers the tags, the values and the key set at once.
            if (resolvedWorkflow[InputsMember] is not JArray declaredInputs)
            {
                return Corrupt(Error(
                    "A plan.json planHashInput resolvedWorkflow inputs is missing or not a JSON array.",
                    "$.*"));
            }
            var declaredIds = new List<string>(declaredInputs.Count);
            var declaredIdSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in declaredInputs)
            {
                if (token is not JObject entry || !HasOnly(entry, s_declaredInputMembers))
                {
                    return Corrupt(Error(
                        "A plan.json planHashInput resolvedWorkflow input is not the v1 form.",
                        "$.*"));
                }
                var declaredId = StringMember(entry[IdMember]);
                var declaredType = StringMember(entry[TypeMember]);
                if (declaredId is null || declaredId.Length == 0 ||
                    declaredType is null || declaredType.Length == 0 ||
                    entry[RequiredMember] is not JValue { Type: JTokenType.Boolean } ||
                    !declaredIdSet.Add(declaredId))
                {
                    return Corrupt(Error(
                        "A plan.json planHashInput resolvedWorkflow input has a missing, empty, mistyped or duplicated member.",
                        "$.*"));
                }
                declaredIds.Add(declaredId);
            }
            if (!MemberEquals(
                hashInput[ExecutionInputsMember],
                PlanHashInputParts.ExecutionInputs(declaredIds, inputs.ToToken())))
            {
                return Review(Error(
                    "A plan.json planHashInput executionInputs member disagrees with the rebuilt form.",
                    "$.*"));
            }

            var fingerprintFailure = CheckFingerprintArray(
                hashInput[UsedTypeFingerprintsMember], s_typeFingerprintMembers);
            if (fingerprintFailure is not null)
            {
                return fingerprintFailure;
            }
            fingerprintFailure = CheckFingerprintArray(
                hashInput[UsedCodecFingerprintsMember], s_codecFingerprintMembers);
            if (fingerprintFailure is not null)
            {
                return fingerprintFailure;
            }

            if (hashInput[EnvironmentIdentifiersMember] is not JObject environment ||
                !HasOnly(environment, s_environmentMembers))
            {
                return Corrupt(Error(
                    "A plan.json planHashInput environmentIdentifiers is not the SG-11 six-member object.",
                    "$.*"));
            }
            foreach (var name in s_environmentMembers)
            {
                if (name == ScriptingDefineSymbolsMember)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(StringMember(environment[name])))
                {
                    return Corrupt(Error(
                        "A plan.json environmentIdentifiers member is missing, empty or not a string.",
                        "$.*"));
                }
            }
            if (environment[ScriptingDefineSymbolsMember] is not JArray defines)
            {
                return Corrupt(Error(
                    "A plan.json scriptingDefineSymbols member is missing or not a JSON array.",
                    "$.*"));
            }
            foreach (var define in defines)
            {
                if (string.IsNullOrEmpty(StringMember(define)))
                {
                    return Corrupt(Error(
                        "A plan.json scriptingDefineSymbols member holds a non-string or empty value.",
                        "$.*"));
                }
            }
            return null;
        }

        // The derivable members of the persisted hash input are rebuilt from
        // the decoded Plan and compared byte-exactly: any edit to a public
        // semantic field that did not also rewrite the hash input fails here,
        // just as any edit to the hash input fails the planHash comparison.
        // Every failure is a public-field/planHashInput contradiction, so the
        // 2026-11-06 SG-12 revalidation classifies them all NeedsReview.
        private static PlanJsonRead? CheckHashInputConsistency(
            JObject hashInput,
            IReadOnlyList<PlanStep> steps,
            IReadOnlyList<ApprovalRequirement> approvals,
            IReadOnlyList<string> effects)
        {
            // Two steps using the same Operation must record one fingerprint —
            // the rebuild takes the first pair, so divergent values would
            // otherwise slip through.
            var fingerprints = new Dictionary<(string Id, int Version), (string Descriptor, string Implementation)>();
            foreach (var step in steps)
            {
                var key = (step.OperationId, step.Version);
                var pair = (step.DescriptorHash, step.ImplementationHash);
                if (!fingerprints.TryAdd(key, pair) && !fingerprints[key].Equals(pair))
                {
                    return Review(Error(
                        "A plan.json gives one Operation two fingerprint pairs.", "$.*"));
                }
            }

            if (!MemberEquals(hashInput[StepsMember], PlanHashInputParts.Steps(steps)) ||
                !MemberEquals(hashInput[TransitionsMember], PlanHashInputParts.Transitions(steps)) ||
                !MemberEquals(hashInput[RetryMember], PlanHashInputParts.Retry(steps)) ||
                !MemberEquals(hashInput[TimeoutsMember], PlanHashInputParts.Timeouts(steps)))
            {
                return Review(Error(
                    "A plan.json step member disagrees with the persisted planHashInput.", "$.*"));
            }
            if (!MemberEquals(
                hashInput[RequiredEffectsMember],
                PlanHashInputParts.RequiredEffects(steps, approvals)) ||
                !MemberEquals(
                    hashInput[UsedOperationFingerprintsMember],
                    PlanHashInputParts.OperationFingerprints(steps)))
            {
                return Review(Error(
                    "A plan.json effects or fingerprint member disagrees with the persisted planHashInput.",
                    "$.*"));
            }

            var union = PlanBuilder.UnionEffects(steps);
            if (union.Count != effects.Count)
            {
                return Review(Error(
                    "A plan.json effects member disagrees with the step effect union.", "$.*"));
            }
            for (var i = 0; i < union.Count; i++)
            {
                if (!string.Equals(union[i], effects[i], StringComparison.Ordinal))
                {
                    return Review(Error(
                        "A plan.json effects member disagrees with the step effect union.", "$.*"));
                }
            }
            return null;
        }

        private static PlanJsonRead? CheckFingerprintArray(JToken? token, HashSet<string> members)
        {
            if (token is not JArray array)
            {
                return Corrupt(Error(
                    "A plan.json fingerprint member is missing or not a JSON array.", "$.*"));
            }
            foreach (var item in array)
            {
                if (item is not JObject entry || !HasOnly(entry, members))
                {
                    return Corrupt(Error(
                        "A plan.json fingerprint entry has a member outside its v1 set.", "$.*"));
                }
                foreach (var property in entry.Properties())
                {
                    var text = StringMember(property.Value);
                    if (string.IsNullOrEmpty(text))
                    {
                        return Corrupt(Error(
                            "A plan.json fingerprint entry has a missing, empty or mistyped value.",
                            "$.*"));
                    }
                    if ((property.Name == "typeDefinitionHash" ||
                         property.Name == "definingAssemblySha256") &&
                        !IsLowerHex64(text))
                    {
                        return Corrupt(Error(
                            "A plan.json fingerprint hash value is not a SHA-256 hex.", "$.*"));
                    }
                }
            }
            return null;
        }

        private static bool MemberEquals(JToken? persisted, JToken rebuilt) =>
            persisted is not null &&
            string.Equals(
                CanonicalJson.Serialize(persisted),
                CanonicalJson.Serialize(rebuilt),
                StringComparison.Ordinal);

        private static void RequireNonEmpty(string? value, string member)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException(member + " must not be null or empty.", "plan");
            }
        }

        private static void RequireLowerHex64(string? value, string member)
        {
            if (!IsLowerHex64(value))
            {
                throw new ArgumentException(
                    member + " must be 64 lowercase hex digits.", "plan");
            }
        }

        private static void RequireIdentifier(string value, string member)
        {
            RequireNonEmpty(value, member);
            if (!value.StartsWith(Identifiers.PlanIdPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException(member + " must start with 'plan_'.", "plan");
            }
        }

        private static void RequireStepIndex(int value, int stepCount, string member)
        {
            if (value < 0 || value >= stepCount)
            {
                throw new ArgumentException(member + " must name an existing step.", "plan");
            }
        }

        private static bool IsLowerHex64(string? value)
        {
            if (value is null || value.Length != 64)
            {
                return false;
            }
            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsVersion(JToken? token, int expected) =>
            token is JValue value && value.Type == JTokenType.Integer &&
                value.Value is long number && number == expected;

        private static string? StringMember(JToken? token) =>
            token is JValue value && value.Type == JTokenType.String &&
                value.Value is string text
                ? text
                : null;

        private static bool IntegerMember(JToken? token, long min, long max, out int integer)
        {
            if (token is JValue value && value.Type == JTokenType.Integer &&
                value.Value is long number && number >= min && number <= max)
            {
                integer = (int)number;
                return true;
            }
            integer = 0;
            return false;
        }

        private static bool NumberMember(JToken? token, out double number)
        {
            if (token is JValue value && value.Value is not null)
            {
                if (value.Type == JTokenType.Integer && value.Value is long integer)
                {
                    number = integer;
                    return true;
                }
                if (value.Type == JTokenType.Float && value.Value is double real)
                {
                    number = real;
                    return true;
                }
            }
            number = 0.0;
            return false;
        }

        private static bool HasOnly(JObject obj, HashSet<string> members)
        {
            foreach (var property in obj.Properties())
            {
                if (!members.Contains(property.Name))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasOnly(JObject obj, params string[] members)
        {
            var allowed = new HashSet<string>(members, StringComparer.Ordinal);
            foreach (var property in obj.Properties())
            {
                if (!allowed.Contains(property.Name))
                {
                    return false;
                }
            }
            return true;
        }

        // Internal diagnostic convention of the SG-02 reader (SPEC §0.6):
        // empty code, payload-free fixed English, JsonPath; the owning layer
        // maps to OW-STORE-002 exactly once before publishing.
        private static Diagnostic Error(string reason, string path) =>
            new Diagnostic(string.Empty, Severity.Error, reason, JsonPath: path);

        private static PlanJsonRead Corrupt(Diagnostic diagnostic) =>
            new PlanJsonRead(PlanJsonStatus.Corrupt, null, diagnostic);

        private static PlanJsonRead Review(Diagnostic diagnostic) =>
            new PlanJsonRead(PlanJsonStatus.NeedsReview, null, diagnostic);
    }
}
