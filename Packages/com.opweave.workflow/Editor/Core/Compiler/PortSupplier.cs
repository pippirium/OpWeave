#nullable enable

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The supplier one input port resolved to under the §A1/R1 rule, as
    /// recorded by the D4 phase on <see cref="ValidationContext.PortSuppliers"/>
    /// for the Build Plan stage (SPEC §D4 item 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the document-level counterpart of the public
    /// <see cref="InputSource"/> family: an <see cref="Edge"/> supplier still
    /// names its source <em>node</em>, because the Plan step index it becomes a
    /// <see cref="FromStep"/> for is only known once the D5 execution order
    /// exists (D4 runs before D5). The Build Plan stage translates each kind —
    /// <see cref="Edge"/> to <see cref="FromStep"/>, <see cref="Input"/> to
    /// <see cref="Compiler.FromInput"/>, <see cref="Parameter"/> to
    /// <see cref="Constant"/>, <see cref="Default"/> to
    /// <see cref="Compiler.Default"/> and <see cref="Absent"/> to
    /// <see cref="Missing"/> — so this type never reaches the SG-12 persisted
    /// form or the planHash input.
    /// </para>
    /// </remarks>
    internal abstract record PortSupplier
    {
        private PortSupplier()
        {
        }

        /// <summary>
        /// A data edge: the value comes from output port <see cref="Port"/> of
        /// node <see cref="Node"/>.
        /// </summary>
        internal sealed record Edge(string Node, string Port) : PortSupplier;

        /// <summary>
        /// A workflow-input binding: the value comes from WF input
        /// <see cref="InputId"/>.
        /// </summary>
        internal sealed record Input(string InputId) : PortSupplier;

        /// <summary>
        /// A constant parameter written on the node; <see cref="Value"/> is the
        /// codec-resolved value when the Resolve stage produced one, else the
        /// written token (SPEC §D0.2).
        /// </summary>
        internal sealed record Parameter(JsonValue Value) : PortSupplier;

        /// <summary>
        /// The Operation Descriptor's declared default, used only because the
        /// port has no explicit supplier (SPEC §A1 item 2).
        /// </summary>
        internal sealed record Default(JsonValue Value) : PortSupplier;

        /// <summary>
        /// No explicit supplier and no Descriptor default: an optional port the
        /// Operation receives as <see cref="Missing"/> — never a JSON
        /// <c>null</c> (SPEC §A0.1).
        /// </summary>
        internal sealed record Absent : PortSupplier;
    }
}
