#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The immutable contract an Operation declares (SPEC §C1): the Validator,
    /// Runner, search index and GUI all read only this record. The positional
    /// constructor is the explicit factory SG-05 reserves for the fields the
    /// <see cref="Op"/> fluent builder does not cover (SPEC §0.6 SG-05).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ControlOutputs"/> defaults to <c>["next"]</c> in v1; other
    /// values are reserved for future control-flow Operations (SPEC §C1 item 2,
    /// appendix N) and rejected by the registration-time checks.
    /// </para>
    /// <para>
    /// <see cref="Kind"/> is the reserved SPEC §N.5.5 (1) / §X.10.4 extension
    /// point: <see cref="OperationKind.Native"/> in v1, while
    /// <see cref="OperationKind.Blueprint"/> is never registrable or executable
    /// (OW-OP-010).
    /// </para>
    /// <para>
    /// The record validates only structural invariants — non-null members and
    /// defensive copies (SPEC §0.4). Every check runs both in the backing-field
    /// initializer (positional construction) and in the corresponding
    /// <c>init</c> accessor (record <c>with</c> expressions and object
    /// initializers), so no construction path can bypass it. Semantic checks
    /// (duplicate ports, unknown side effects, summary length, …) are
    /// <see cref="OperationDescriptorValidator"/>'s registration-time contract
    /// (SPEC §C3 rule 3).
    /// </para>
    /// </remarks>
    public sealed record OperationDescriptor(
        string Id, int Version,
        string DisplayName, string Summary, string Description,
        IReadOnlyDictionary<string, LocalizedText> Localized,
        string Category, IReadOnlyList<string> Tags, IReadOnlyList<string> Keywords,
        IReadOnlyList<PortDescriptor> Inputs, IReadOnlyList<PortDescriptor> Outputs,
        IReadOnlyList<string> ControlOutputs,
        ExecutionMode ExecutionMode,
        IReadOnlyList<string> SideEffects,
        bool Destructive,
        UndoSupport Undo,
        Idempotency Idempotency,
        RetryPolicy DefaultRetry,
        int DefaultTimeoutSeconds,
        IReadOnlyList<Requirement> Requirements,
        TypeRefinement? OutputRefinement,
        IReadOnlyList<OperationExample> Examples,
        OperationKind Kind = OperationKind.Native)
    {
        /// <summary>The SPEC §E6 item 1 default step timeout for an Immediate Operation (seconds).</summary>
        internal const int DefaultImmediateTimeoutSeconds = 30;

        /// <summary>The SPEC §E6 item 1 default step timeout for a Waitable Operation (seconds).</summary>
        internal const int DefaultWaitableTimeoutSeconds = 600;

        private readonly string _id = CheckId(Id);
        private readonly string _displayName = NotNull(DisplayName, nameof(DisplayName));
        private readonly string _summary = NotNull(Summary, nameof(Summary));
        private readonly string _description = NotNull(Description, nameof(Description));
        private readonly IReadOnlyDictionary<string, LocalizedText> _localized =
            CheckLocalized(Localized);
        private readonly string _category = NotNull(Category, nameof(Category));
        private readonly IReadOnlyList<string> _tags = CheckStrings(Tags, nameof(Tags));
        private readonly IReadOnlyList<string> _keywords =
            CheckStrings(Keywords, nameof(Keywords));
        private readonly IReadOnlyList<PortDescriptor> _inputs =
            CheckPorts(Inputs, nameof(Inputs));
        private readonly IReadOnlyList<PortDescriptor> _outputs =
            CheckPorts(Outputs, nameof(Outputs));
        private readonly IReadOnlyList<string> _controlOutputs =
            CheckStrings(ControlOutputs, nameof(ControlOutputs));
        private readonly IReadOnlyList<string> _sideEffects =
            CheckStrings(SideEffects, nameof(SideEffects));
        private readonly RetryPolicy _defaultRetry =
            NotNull(DefaultRetry, nameof(DefaultRetry));
        private readonly IReadOnlyList<Requirement> _requirements =
            CheckRequirements(Requirements);
        private readonly IReadOnlyList<OperationExample> _examples =
            CheckExamples(Examples);

        /// <summary>The Operation ID (SPEC §A4 grammar, e.g. <c>unity.gameobject.create</c>).</summary>
        public string Id
        {
            get => _id;
            init => _id = CheckId(value);
        }

        /// <summary>The English display name.</summary>
        public string DisplayName
        {
            get => _displayName;
            init => _displayName = NotNull(value, nameof(DisplayName));
        }

        /// <summary>The English summary (at most 120 characters, checked at registration).</summary>
        public string Summary
        {
            get => _summary;
            init => _summary = NotNull(value, nameof(Summary));
        }

        /// <summary>The English description.</summary>
        public string Description
        {
            get => _description;
            init => _description = NotNull(value, nameof(Description));
        }

        /// <summary>Localized display text keyed by language tag such as <c>"ja"</c> (R8).</summary>
        public IReadOnlyDictionary<string, LocalizedText> Localized
        {
            get => _localized;
            init => _localized = CheckLocalized(value);
        }

        /// <summary>The search category.</summary>
        public string Category
        {
            get => _category;
            init => _category = NotNull(value, nameof(Category));
        }

        /// <summary>Search tags.</summary>
        public IReadOnlyList<string> Tags
        {
            get => _tags;
            init => _tags = CheckStrings(value, nameof(Tags));
        }

        /// <summary>English search keywords.</summary>
        public IReadOnlyList<string> Keywords
        {
            get => _keywords;
            init => _keywords = CheckStrings(value, nameof(Keywords));
        }

        /// <summary>The input ports.</summary>
        public IReadOnlyList<PortDescriptor> Inputs
        {
            get => _inputs;
            init => _inputs = CheckPorts(value, nameof(Inputs));
        }

        /// <summary>The output ports.</summary>
        public IReadOnlyList<PortDescriptor> Outputs
        {
            get => _outputs;
            init => _outputs = CheckPorts(value, nameof(Outputs));
        }

        /// <summary>Control output port names; v1 Operations use <c>["next"]</c> (SPEC §C1 item 2).</summary>
        public IReadOnlyList<string> ControlOutputs
        {
            get => _controlOutputs;
            init => _controlOutputs = CheckStrings(value, nameof(ControlOutputs));
        }

        /// <summary>Declared side effects drawn from the closed SPEC §C4 vocabulary.</summary>
        public IReadOnlyList<string> SideEffects
        {
            get => _sideEffects;
            init => _sideEffects = CheckStrings(value, nameof(SideEffects));
        }

        /// <summary>The retry contract applied when a node does not override it (SPEC §E7).</summary>
        public RetryPolicy DefaultRetry
        {
            get => _defaultRetry;
            init => _defaultRetry = NotNull(value, nameof(DefaultRetry));
        }

        /// <summary>Environmental preconditions (SPEC §D6, §G1).</summary>
        public IReadOnlyList<Requirement> Requirements
        {
            get => _requirements;
            init => _requirements = CheckRequirements(value);
        }

        /// <summary>Complete example workflows returned by describe_operation (SPEC §C8).</summary>
        public IReadOnlyList<OperationExample> Examples
        {
            get => _examples;
            init => _examples = CheckExamples(value);
        }

        private static string CheckId(string id) =>
            string.IsNullOrEmpty(id)
                ? throw new ArgumentException(
                    "Operation ID must not be null or empty.", nameof(Id))
                : id;

        private static T NotNull<T>(T? value, string name) where T : class =>
            value ?? throw new ArgumentNullException(name);

        private static IReadOnlyDictionary<string, LocalizedText> CheckLocalized(
            IReadOnlyDictionary<string, LocalizedText> localized)
        {
            var copy = ReadOnlyCollections.CopyMap(localized);
            foreach (var pair in copy)
            {
                if (pair.Value is null)
                {
                    throw new ArgumentException(
                        "Localized text values must not be null.", nameof(localized));
                }
            }
            return copy;
        }

        private static IReadOnlyList<string> CheckStrings(
            IReadOnlyList<string> values, string name)
        {
            var copy = ReadOnlyCollections.CopyList(values);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "List entries must not be null.", name);
                }
            }
            return copy;
        }

        private static IReadOnlyList<PortDescriptor> CheckPorts(
            IReadOnlyList<PortDescriptor> ports, string name)
        {
            var copy = ReadOnlyCollections.CopyList(ports);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Port entries must not be null.", name);
                }
            }
            return copy;
        }

        private static IReadOnlyList<Requirement> CheckRequirements(
            IReadOnlyList<Requirement> requirements)
        {
            var copy = ReadOnlyCollections.CopyList(requirements);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Requirement entries must not be null.", nameof(requirements));
                }
            }
            return copy;
        }

        private static IReadOnlyList<OperationExample> CheckExamples(
            IReadOnlyList<OperationExample> examples)
        {
            var copy = ReadOnlyCollections.CopyList(examples);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Example entries must not be null.", nameof(examples));
                }
            }
            return copy;
        }
    }
}
