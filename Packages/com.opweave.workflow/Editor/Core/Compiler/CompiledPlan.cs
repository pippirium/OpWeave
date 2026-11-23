#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The immutable plan a validated workflow compiles into (SPEC §D7): the
    /// only artifact the Runner reads. <see cref="PlanHash"/> is the SHA-256 of
    /// the plan's canonical execution meaning — the SPEC §0.6 SG-12 twelve
    /// named inputs (canonicalJsonVersion, resolvedWorkflow, executionInputs,
    /// steps, transitions, retry, timeouts, requiredEffects,
    /// usedOperationFingerprints, usedTypeFingerprints, usedCodecFingerprints,
    /// environmentIdentifiers) — so the Plan that was validated and the Plan
    /// that executes are provably identical (SPEC §D7 item 5, §D0.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the Compiler and <c>RunStore.Load</c> create instances; there is no
    /// public constructor. The Plan is built exclusively from
    /// <see cref="JsonValue"/> and defensively copied collections — no
    /// <c>WorkflowDocument</c> object is referenced, so mutating source tokens
    /// after the build changes nothing (SPEC §D7 item 4, R5).
    /// </para>
    /// <para>
    /// <see cref="WorkflowHash"/> and <see cref="RegistryHash"/> are stored for
    /// audit but are never planHash inputs (SPEC §D0.2). <see cref="SourceMap"/>
    /// is the reserved SPEC §N.5.5 item 2 slot: v1 has no Blueprint expansion
    /// and the list stays empty.
    /// </para>
    /// </remarks>
    public sealed class CompiledPlan
    {
        internal CompiledPlan(
            string planId,
            string planHash,
            string workflowId,
            string workflowHash,
            string registryHash,
            JsonValue inputs,
            IReadOnlyList<PlanStep> steps,
            IReadOnlyList<PlanOutput> outputs,
            IReadOnlyList<string> effects,
            IReadOnlyList<ApprovalRequirement> approvals,
            IReadOnlyList<PlanSourceMapEntry> sourceMap,
            JsonValue planHashInput)
        {
            PlanId = planId;
            PlanHash = planHash;
            WorkflowId = workflowId;
            WorkflowHash = workflowHash;
            RegistryHash = registryHash;
            Inputs = inputs;
            Steps = ReadOnlyCollections.CopyList(steps);
            Outputs = ReadOnlyCollections.CopyList(outputs);
            Effects = ReadOnlyCollections.CopyList(effects);
            Approvals = ReadOnlyCollections.CopyList(approvals);
            SourceMap = ReadOnlyCollections.CopyList(sourceMap);
            PlanHashInput = planHashInput;
        }

        /// <summary>The Plan data-structure version: 1 (SPEC §D7, §N.1).</summary>
        public int FormatVersion => 1;

        /// <summary>The canonicalization algorithm version: 1 (SPEC §0.3, §D7).</summary>
        public int CanonicalJsonVersion => CanonicalJson.Version;

        /// <summary>The Plan identifier: <c>"plan_" + ULID</c> (SPEC §D7).</summary>
        public string PlanId { get; }

        /// <summary>Lowercase-hex SHA-256 of the canonical execution meaning (SPEC §D7 item 5).</summary>
        public string PlanHash { get; }

        /// <summary>The workflow document's declared id.</summary>
        public string WorkflowId { get; }

        /// <summary>SHA-256 of the §A4 canonical form; stored, never hashed (SPEC §D0.2).</summary>
        public string WorkflowHash { get; }

        /// <summary>The registry content hash; audit only, never hashed (SPEC §D0.1).</summary>
        public string RegistryHash { get; }

        /// <summary>The codec-resolved execution inputs the Plan was built with.</summary>
        public JsonValue Inputs { get; }

        /// <summary>The steps in D5 execution order.</summary>
        public IReadOnlyList<PlanStep> Steps { get; }

        /// <summary>The workflow outputs resolved to step positions.</summary>
        public IReadOnlyList<PlanOutput> Outputs { get; }

        /// <summary>The union of every step's actual effects (SPEC §C4, §D6).</summary>
        public IReadOnlyList<string> Effects { get; }

        /// <summary>The <c>ask</c> approvals the Run requires (SPEC §D6 item 1, §H4 item 3).</summary>
        public IReadOnlyList<ApprovalRequirement> Approvals { get; }

        /// <summary>
        /// The reserved hierarchical-display SourceMap slot (SPEC §N.5.5 item 2);
        /// always empty in v1.
        /// </summary>
        public IReadOnlyList<PlanSourceMapEntry> SourceMap { get; }

        /// <summary>
        /// The canonical SG-12 twelve-field object <see cref="PlanHash"/> hashes —
        /// retained so plan.json round-tripping and §D0.1 revalidation recompute
        /// the identical input (SPEC §D7 items 5–6).
        /// </summary>
        internal JsonValue PlanHashInput { get; }
    }
}
