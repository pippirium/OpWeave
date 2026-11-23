#nullable enable

using System.Collections.Generic;
using OpWeave.Core.Operations;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One port row of the SPEC §M.4 create dialog: a name and a Type ID.
    /// </summary>
    internal sealed class OperationCreatePort
    {
        /// <summary>The port name (the §A4 scoped id grammar).</summary>
        internal string Name = string.Empty;

        /// <summary>
        /// The Type ID picked from the published TypeRegistry's candidates
        /// (SPEC §M.4 item 1 — the same data <c>list_types</c> exposes).
        /// </summary>
        internal string TypeId = string.Empty;

        /// <summary>True for an output port, false for an input port.</summary>
        internal bool IsOutput;
    }

    /// <summary>
    /// The editable inputs of the SPEC §M.4 "Assets > Create > OpWeave >
    /// Operation" dialog — one member per input item of SPEC §M.4 item 1.
    /// There is no Operation ID input: the ID is generated once at commit
    /// from a UUID v4 (SPEC §A4, <see cref="OperationIdGenerator"/>).
    /// The dialog mutates these fields and re-plans live; nothing here is
    /// validated yet — that is <see cref="OperationCreatePlanner"/>'s job.
    /// </summary>
    internal sealed class OperationCreateRequest
    {
        /// <summary>The Operation version; the dialog generates version 1.</summary>
        internal int Version = 1;

        /// <summary>The English display name (SPEC §M.4 表示名).</summary>
        internal string DisplayName = string.Empty;

        /// <summary>The English summary (SPEC §C1, at most 120 characters).</summary>
        internal string Summary = string.Empty;

        /// <summary>The Japanese display name (SPEC §M.4 日本語表示名).</summary>
        internal string DisplayNameJa = string.Empty;

        /// <summary>The Japanese summary that rides the <c>.Ja(...)</c> call.</summary>
        internal string SummaryJa = string.Empty;

        /// <summary>The category (SPEC §M.4 カテゴリ).</summary>
        internal string Category = string.Empty;

        /// <summary>The SPEC §C1 ExecutionMode (Immediate or Waitable).</summary>
        internal ExecutionMode Mode = ExecutionMode.Immediate;

        /// <summary>The declared side effects from the closed §C4 vocabulary.</summary>
        internal readonly List<string> Effects = new List<string>();

        /// <summary>The declared idempotency; <see cref="Idempotency.Unsafe"/> by default (SPEC §M.4).</summary>
        internal Idempotency Idempotency = Idempotency.Unsafe;

        /// <summary>The input/output ports declared in the dialog.</summary>
        internal readonly List<OperationCreatePort> Ports = new List<OperationCreatePort>();

        /// <summary>"テストも作る" — create the contract test file; default on (SPEC §M.4).</summary>
        internal bool CreateTests = true;

        /// <summary>The asset-relative folder the files are written into.</summary>
        internal string TargetFolder = string.Empty;
    }
}
