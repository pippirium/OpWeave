#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The fluent builder behind <see cref="Op.Define"/> (SPEC §0.6 SG-05). Its
    /// public method set is exactly the owner-approved v1 surface —
    /// <see cref="Summary"/>, <see cref="Ja"/>, <see cref="Input"/>,
    /// <see cref="Output"/>, <see cref="Effects"/>, <see cref="Undo"/>,
    /// <see cref="Idempotency"/>, <see cref="Requires"/> and <see cref="Build"/> —
    /// and must not grow undocumented fluent members.
    /// </summary>
    /// <remarks>
    /// Fields without a fluent method keep these builder defaults until the
    /// <see cref="OperationDescriptor"/> record is assembled directly:
    /// <c>DisplayName</c>/<c>Description</c>/<c>Category</c> empty,
    /// <c>Tags</c>/<c>Keywords</c>/<c>Examples</c> empty,
    /// <c>ControlOutputs</c> <c>["next"]</c>, <see cref="ExecutionMode.Immediate"/>,
    /// <c>Destructive</c> false, <see cref="Operations.Idempotency.Unsafe"/>
    /// (the conservative SPEC §M.4 default a creator must deliberately
    /// strengthen), <see cref="Operations.UndoSupport.None"/>, a default
    /// <see cref="RetryPolicy"/>, <c>DefaultTimeoutSeconds</c> 30 (the SPEC §E6
    /// Immediate default), <see cref="OperationKind.Native"/>.
    /// </remarks>
    public sealed class OperationDescriptorBuilder
    {
        private readonly string _id;
        private readonly int _version;
        private string _summary = string.Empty;
        private readonly Dictionary<string, LocalizedText> _localized =
            new Dictionary<string, LocalizedText>(StringComparer.Ordinal);
        private readonly List<PortDescriptor> _inputs = new List<PortDescriptor>();
        private readonly List<PortDescriptor> _outputs = new List<PortDescriptor>();
        private readonly List<string> _sideEffects = new List<string>();
        private readonly List<Requirement> _requirements = new List<Requirement>();
        private UndoSupport _undo = UndoSupport.None;
        private Idempotency _idempotency = OpWeave.Core.Operations.Idempotency.Unsafe;

        internal OperationDescriptorBuilder(string id, int version)
        {
            _id = id;
            _version = version;
        }

        /// <summary>Sets the English summary (at most 120 characters, enforced at registration).</summary>
        public OperationDescriptorBuilder Summary(string summary)
        {
            _summary = summary ?? throw new ArgumentNullException(nameof(summary));
            return this;
        }

        /// <summary>Sets the Japanese display name, summary and search keywords (SPEC §C1 item 1, R8).</summary>
        public OperationDescriptorBuilder Ja(
            string displayName, string summary, params string[] keywords)
        {
            _localized["ja"] = new LocalizedText(displayName, summary, keywords);
            return this;
        }

        /// <summary>
        /// Adds an input port. <paramref name="constraints"/> configures the
        /// <see cref="PortConstraintsBuilder"/> contract of SPEC §0.6 SG-05;
        /// <paramref name="default"/> pins a constant default value.
        /// </summary>
        public OperationDescriptorBuilder Input(
            string name,
            string type,
            bool required,
            Action<PortConstraintsBuilder>? constraints = null,
            JsonValue? @default = null)
        {
            PortConstraints? built = null;
            if (constraints is not null)
            {
                var constraintBuilder = new PortConstraintsBuilder();
                constraints(constraintBuilder);
                built = constraintBuilder.Build();
            }
            _inputs.Add(new PortDescriptor(
                name, type, required, @default, string.Empty, built));
            return this;
        }

        /// <summary>Adds an output port.</summary>
        public OperationDescriptorBuilder Output(string name, string type)
        {
            _outputs.Add(new PortDescriptor(
                name, type, Required: false, Default: null, string.Empty, Constraints: null));
            return this;
        }

        /// <summary>Appends declared side effects from the closed SPEC §C4 vocabulary.</summary>
        public OperationDescriptorBuilder Effects(params string[] effects)
        {
            if (effects is null)
            {
                throw new ArgumentNullException(nameof(effects));
            }
            _sideEffects.AddRange(effects);
            return this;
        }

        /// <summary>Sets the declared Undo support level (SPEC §C1, §G3).</summary>
        public OperationDescriptorBuilder Undo(UndoSupport undo)
        {
            _undo = undo;
            return this;
        }

        /// <summary>Sets the declared idempotency contract (SPEC §F5).</summary>
        public OperationDescriptorBuilder Idempotency(Idempotency idempotency)
        {
            _idempotency = idempotency;
            return this;
        }

        /// <summary>Appends an environmental precondition (SPEC §D6, §G1).</summary>
        public OperationDescriptorBuilder Requires(string kind, string? port = null)
        {
            _requirements.Add(new Requirement(kind, port));
            return this;
        }

        /// <summary>
        /// Assembles the immutable <see cref="OperationDescriptor"/>. The result
        /// is not yet validated — malformed descriptors are rejected at
        /// registration with OW-OP-003 (SPEC §0.6 SG-05, §C3 rule 3).
        /// </summary>
        public OperationDescriptor Build() =>
            new OperationDescriptor(
                _id,
                _version,
                DisplayName: string.Empty,
                Summary: _summary,
                Description: string.Empty,
                Localized: _localized,
                Category: string.Empty,
                Tags: Array.Empty<string>(),
                Keywords: Array.Empty<string>(),
                Inputs: _inputs,
                Outputs: _outputs,
                ControlOutputs: new[] { "next" },
                ExecutionMode: ExecutionMode.Immediate,
                SideEffects: _sideEffects,
                Destructive: false,
                Undo: _undo,
                Idempotency: _idempotency,
                DefaultRetry: new RetryPolicy(),
                DefaultTimeoutSeconds: OperationDescriptor.DefaultImmediateTimeoutSeconds,
                Requirements: _requirements,
                OutputRefinement: null,
                Examples: Array.Empty<OperationExample>());
    }
}
