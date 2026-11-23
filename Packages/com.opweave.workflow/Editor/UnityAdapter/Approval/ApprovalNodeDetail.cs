#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Registry;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The per-node approval detail of SPEC §H4 item 3 (T-W3.4-02): everything
    /// the approval surface shows for one node whose side effects need a human
    /// decision — the Operation identity, the step's actual effects and the
    /// subset that asked, the Operation's origin and source (defining
    /// assembly, provenance source file and defining UPM package and its
    /// resolved commit for custom/external Operations), and the inputs the
    /// Plan fixes.
    /// </summary>
    /// <remarks>
    /// <see cref="ConstantInputs"/> carries the literal port values verbatim —
    /// the paths, the full generated code and the package identifier the human
    /// reads before deciding. A Git URL embedding credentials can never appear
    /// here: SPEC §G8.1 rejects it with OW-OP-006 before the Plan is created
    /// or persisted, so no Plan can carry one. <see cref="BoundInputs"/> marks
    /// the ports fed by a workflow input or an upstream step;
    /// <see cref="ResolvedInputs"/> carries the value each workflow-input-bound
    /// port executes with — a Plan-fixed value the human inspects exactly like
    /// a constant (the execution inputs are a planHash input, so the presented
    /// value is the approved one). An upstream-step port's value does not
    /// exist before its producer runs and stays a binding marker only.
    /// </remarks>
    public sealed class ApprovalNodeDetail
    {
        internal ApprovalNodeDetail(
            string nodeId,
            string operationId,
            int version,
            IReadOnlyList<string> effects,
            IReadOnlyList<string> approvalEffects,
            OperationOrigin? origin,
            string? assemblyName,
            string? sourceFile,
            string? packageId,
            string? resolvedCommit,
            IReadOnlyDictionary<string, JsonValue> constantInputs,
            IReadOnlyDictionary<string, string> boundInputs,
            IReadOnlyDictionary<string, InputValue> resolvedInputs)
        {
            NodeId = nodeId;
            OperationId = operationId;
            Version = version;
            Effects = effects;
            ApprovalEffects = approvalEffects;
            Origin = origin;
            AssemblyName = assemblyName;
            SourceFile = sourceFile;
            PackageId = packageId;
            ResolvedCommit = resolvedCommit;
            ConstantInputs = constantInputs;
            BoundInputs = boundInputs;
            ResolvedInputs = resolvedInputs;
        }

        /// <summary>The workflow node the decision covers.</summary>
        public string NodeId { get; }

        /// <summary>The Operation the node's step runs (SPEC §D7).</summary>
        public string OperationId { get; }

        /// <summary>The Operation version the node's step runs.</summary>
        public int Version { get; }

        /// <summary>The step's actual effects (SPEC §D6 item 1, §C4).</summary>
        public IReadOnlyList<string> Effects { get; }

        /// <summary>
        /// The effects that asked for the human decision — the node's entries
        /// in the Plan's <c>approvals</c>, in Plan order.
        /// </summary>
        public IReadOnlyList<string> ApprovalEffects { get; }

        /// <summary>
        /// The Operation's registration origin (SPEC §C3) — Standard or
        /// Custom — or null when the current Registry no longer resolves it
        /// (the §E2 item 5 invalidation then ends the Run anyway).
        /// </summary>
        public OperationOrigin? Origin { get; }

        /// <summary>
        /// The simple name of the assembly the Operation is defined in — the
        /// source of a custom or external Operation (SPEC §H4 item 3). Null
        /// when the Registry does not resolve the Operation.
        /// </summary>
        public string? AssemblyName { get; }

        /// <summary>
        /// The project-relative source file kept for provenance display
        /// (SPEC §C2 item 2), or null.
        /// </summary>
        public string? SourceFile { get; }

        /// <summary>
        /// The UPM Package ID of the package defining the Operation
        /// (SPEC §C3, §X.3), or null for a project assembly.
        /// </summary>
        public string? PackageId { get; }

        /// <summary>
        /// The full resolved commit the defining package was installed at
        /// (SPEC §C3, §X.3), or null when the package manager records none.
        /// </summary>
        public string? ResolvedCommit { get; }

        /// <summary>
        /// The port values the Plan fixes, keyed by port name — Constant and
        /// Descriptor-default inputs verbatim (paths, full code text, package
        /// identifiers). Credential-bearing Git URLs are excluded by
        /// construction (SPEC §G8.1).
        /// </summary>
        public IReadOnlyDictionary<string, JsonValue> ConstantInputs { get; }

        /// <summary>
        /// The ports bound to a workflow input or an upstream step's output,
        /// keyed by port name: <c>"input:&lt;inputId&gt;"</c> or
        /// <c>"step[&lt;index&gt;].&lt;port&gt;"</c> — the binding; the value
        /// an <c>input:</c> port executes with is in
        /// <see cref="ResolvedInputs"/> (SPEC §D7).
        /// </summary>
        public IReadOnlyDictionary<string, string> BoundInputs { get; }

        /// <summary>
        /// The resolved Plan value of every <c>input:</c>-bound port, keyed by
        /// port name — the workflow input's member of the Plan's execution
        /// inputs the approval is bound to (SPEC §H4 item 3): the paths and
        /// the full code the human reads before deciding when the literal is
        /// not written into the workflow itself. The same resolution the run
        /// loop applies (SPEC §E5): a supplied member is
        /// <see cref="InputValue.Present"/> — an explicit JSON <c>null</c>
        /// stays a present value (SPEC §A0.1) — and an absent member is
        /// <see cref="InputValue.Missing"/>, never a JSON <c>null</c>. A
        /// credential-bearing Git URL cannot appear here either: §G8.1's
        /// check runs before the Plan exists and the
        /// <c>unity.package.add</c> <c>id</c> port is ConstantOnly, so no
        /// workflow-input binding can feed one. Step-bound ports carry no
        /// entry — their value does not exist before the producer runs.
        /// </summary>
        public IReadOnlyDictionary<string, InputValue> ResolvedInputs { get; }
    }
}
