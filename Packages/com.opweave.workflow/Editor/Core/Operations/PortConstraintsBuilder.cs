#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The constraint callback argument of
    /// <see cref="OperationDescriptorBuilder.Input"/> (SPEC §0.6 SG-05). Its
    /// public method set is exactly <see cref="Enum"/>, <see cref="MaxLength"/>,
    /// <see cref="Pattern"/>, <see cref="Min"/>, <see cref="Max"/> and
    /// <see cref="ConstantOnly"/>.
    /// </summary>
    public sealed class PortConstraintsBuilder
    {
        private IReadOnlyList<string>? _enum;
        private string? _pattern;
        private double? _min;
        private double? _max;
        private int? _maxLength;
        private bool _constantOnly;

        internal PortConstraintsBuilder()
        {
        }

        /// <summary>Restricts the port to the listed constant values.</summary>
        public PortConstraintsBuilder Enum(params string[] values)
        {
            _enum = values ?? throw new ArgumentNullException(nameof(values));
            return this;
        }

        /// <summary>Sets the maximum string length.</summary>
        public PortConstraintsBuilder MaxLength(int value)
        {
            _maxLength = value;
            return this;
        }

        /// <summary>Sets the regular-expression pattern the value must match.</summary>
        public PortConstraintsBuilder Pattern(string pattern)
        {
            _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            return this;
        }

        /// <summary>Sets the inclusive minimum for numeric values.</summary>
        public PortConstraintsBuilder Min(double value)
        {
            _min = value;
            return this;
        }

        /// <summary>Sets the inclusive maximum for numeric values.</summary>
        public PortConstraintsBuilder Max(double value)
        {
            _max = value;
            return this;
        }

        /// <summary>
        /// Pins the port to constant values only — no data edges or WF-input
        /// bindings (SPEC §C1 item 3, e.g. <c>unity.script.create</c>'s
        /// <c>content</c>, <c>unity.package.add</c>'s <c>id</c>).
        /// </summary>
        public PortConstraintsBuilder ConstantOnly()
        {
            _constantOnly = true;
            return this;
        }

        internal PortConstraints Build() =>
            new PortConstraints(_enum, _pattern, _min, _max, _maxLength, _constantOnly);
    }
}
