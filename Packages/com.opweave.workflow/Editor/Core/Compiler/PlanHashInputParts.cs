#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The PlanStep/Approval/Input-derived members of the SG-12 twelve-field
    /// planHash input (SPEC §0.6 SG-12, §D7 item 5): <c>executionInputs</c>,
    /// <c>steps</c>, <c>transitions</c>, <c>retry</c>, <c>timeouts</c>,
    /// <c>requiredEffects</c> and <c>usedOperationFingerprints</c>, which the
    /// Build Plan stage assembles once and plan.json loading rebuilds from
    /// the persisted public fields — plus the persisted
    /// <c>planHashInput.resolvedWorkflow</c> for the declared input ids — to
    /// prove they encode the identical execution meaning (T-W1.6-08). The
    /// remaining five input fields — <c>canonicalJsonVersion</c>,
    /// <c>resolvedWorkflow</c>, <c>usedTypeFingerprints</c>,
    /// <c>usedCodecFingerprints</c> and <c>environmentIdentifiers</c> — are not
    /// derivable from the Plan's public surface and stay owned by the
    /// compiler / the persisted <c>planHashInput</c> member.
    /// </summary>
    /// <remarks>
    /// Local, reversible member shapes frozen by the golden vectors of
    /// local:T-W1.6-07:plan-hash: <c>steps</c>, <c>transitions</c>,
    /// <c>retry</c> and <c>timeouts</c> are arrays aligned by step index; a
    /// step entry carries <c>index</c>, <c>nodeId</c>, <c>operationId</c>,
    /// <c>version</c>, the snake_case <c>idempotency</c>, its SG-12
    /// <c>inputs</c> tags and its resolved output Type IDs under
    /// <c>outputs</c>; <c>requiredEffects</c> is
    /// <c>{"steps":[{index,effects}],"approvals":[{nodeId,effect,detail}]}</c>;
    /// each operation fingerprint is
    /// <c>{id,version,descriptorHash,implementationHash,coreApiVersion}</c>
    /// with the used (id, version) pairs deduplicated and Ordinal-sorted.
    /// </remarks>
    internal static class PlanHashInputParts
    {
        /// <summary>
        /// The <c>executionInputs</c> member: every declared WF input keyed
        /// to an unambiguous tag — a supplied one as
        /// <c>{"kind":"supplied","value":&lt;resolved value&gt;}</c> and an
        /// absent one as the SG-12 <c>{"kind":"missing"}</c>, so a supplied
        /// <c>core.Json</c> value shaped like a sentinel still hashes
        /// distinctly from Missing (SPEC §0.6 SG-12). Undeclared supplied
        /// members carry no execution meaning (OW-VAL-014) and are excluded;
        /// a non-object supplied payload supplies nothing member-wise.
        /// </summary>
        internal static JObject ExecutionInputs(
            IReadOnlyList<string> declaredInputIds, JToken? supplied)
        {
            var suppliedObject = supplied as JObject;
            var inputs = new JObject();
            foreach (var id in declaredInputIds)
            {
                inputs[id] = suppliedObject is not null &&
                    suppliedObject.TryGetValue(id, out var value)
                        ? new JObject { ["kind"] = "supplied", ["value"] = value }
                        : InputSourceJson.Write(new Missing()).ToToken();
            }
            return inputs;
        }

        /// <summary>The <c>steps</c> member: one entry per PlanStep in index order.</summary>
        internal static JArray Steps(IReadOnlyList<PlanStep> steps)
        {
            var array = new JArray();
            foreach (var step in steps)
            {
                var inputs = new JObject();
                foreach (var pair in step.Inputs)
                {
                    inputs[pair.Key] = InputSourceJson.Write(pair.Value).ToToken();
                }
                var outputs = new JObject();
                foreach (var pair in step.OutputTypes)
                {
                    outputs[pair.Key] = pair.Value;
                }
                array.Add(new JObject
                {
                    ["index"] = step.Index,
                    ["nodeId"] = step.NodeId,
                    ["operationId"] = step.OperationId,
                    ["version"] = step.Version,
                    ["idempotency"] = JsonNaming.ToSnakeCase(step.Idempotency.ToString()),
                    ["inputs"] = inputs,
                    ["outputs"] = outputs,
                });
            }
            return array;
        }

        /// <summary>The <c>transitions</c> member: the per-step port→next-step objects.</summary>
        internal static JArray Transitions(IReadOnlyList<PlanStep> steps)
        {
            var array = new JArray();
            foreach (var step in steps)
            {
                var transitions = new JObject();
                foreach (var pair in step.Transitions)
                {
                    transitions[pair.Key] = pair.Value;
                }
                array.Add(transitions);
            }
            return array;
        }

        /// <summary>The <c>retry</c> member: the per-step retry objects.</summary>
        internal static JArray Retry(IReadOnlyList<PlanStep> steps)
        {
            var array = new JArray();
            foreach (var step in steps)
            {
                array.Add(new JObject
                {
                    ["maxAttempts"] = step.Retry.MaxAttempts,
                    ["initialBackoffMs"] = step.Retry.InitialBackoffMs,
                    ["multiplier"] = step.Retry.Multiplier,
                    ["maxBackoffMs"] = step.Retry.MaxBackoffMs,
                });
            }
            return array;
        }

        /// <summary>The <c>timeouts</c> member: the per-step timeout integers.</summary>
        internal static JArray Timeouts(IReadOnlyList<PlanStep> steps)
        {
            var array = new JArray();
            foreach (var step in steps)
            {
                array.Add(step.TimeoutSeconds);
            }
            return array;
        }

        /// <summary>
        /// The <c>requiredEffects</c> member: the per-step actual effects plus
        /// the approval list (SPEC §D6 item 1).
        /// </summary>
        internal static JObject RequiredEffects(
            IReadOnlyList<PlanStep> steps,
            IReadOnlyList<ApprovalRequirement> approvals)
        {
            var stepsArray = new JArray();
            foreach (var step in steps)
            {
                var effects = new JArray();
                foreach (var effect in step.Effects)
                {
                    effects.Add(effect);
                }
                stepsArray.Add(new JObject { ["index"] = step.Index, ["effects"] = effects });
            }

            var approvalsArray = new JArray();
            foreach (var requirement in approvals)
            {
                approvalsArray.Add(new JObject
                {
                    ["nodeId"] = requirement.NodeId,
                    ["effect"] = requirement.Effect,
                    ["detail"] = requirement.Detail,
                });
            }
            return new JObject
            {
                ["steps"] = stepsArray,
                ["approvals"] = approvalsArray,
            };
        }

        /// <summary>
        /// The <c>usedOperationFingerprints</c> member: one
        /// <c>{id,version,descriptorHash,implementationHash,coreApiVersion}</c>
        /// entry per distinct (operationId, version) the steps use, sorted by
        /// id then version Ordinal (SPEC §0.6 SG-12, §D0.1). Every used
        /// Operation appears on at least one step, and a valid Plan gives the
        /// same pair identical fingerprint values.
        /// </summary>
        internal static JArray OperationFingerprints(IReadOnlyList<PlanStep> steps)
        {
            var used = new Dictionary<(string Id, int Version), PlanStep>();
            foreach (var step in steps)
            {
                used.TryAdd((step.OperationId, step.Version), step);
            }
            var ordered = new List<PlanStep>(used.Values);
            ordered.Sort((a, b) =>
            {
                var byId = string.Compare(a.OperationId, b.OperationId, StringComparison.Ordinal);
                return byId != 0 ? byId : a.Version.CompareTo(b.Version);
            });

            var array = new JArray();
            foreach (var step in ordered)
            {
                array.Add(new JObject
                {
                    ["id"] = step.OperationId,
                    ["version"] = step.Version,
                    ["descriptorHash"] = step.DescriptorHash,
                    ["implementationHash"] = step.ImplementationHash,
                    ["coreApiVersion"] = CoreApi.Version,
                });
            }
            return array;
        }
    }
}
