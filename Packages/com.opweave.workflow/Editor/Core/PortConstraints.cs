#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// Optional per-port value constraints of SPEC §C1: an enum allow-list, a
    /// regular-expression pattern, numeric bounds, a maximum length and the
    /// <see cref="ConstantOnly"/> flag that pins a port to constant values
    /// (no data edges or WF-input bindings; e.g. <c>unity.script.create</c>'s
    /// <c>content</c>, <c>unity.package.add</c>'s <c>id</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="IValueCodec.Validate"/> receives these constraints so Codec
    /// validation can check them at conversion time (SPEC §B7); enforcement of
    /// <see cref="ConstantOnly"/> against data edges is a graph-validation
    /// concern (OW-OP-006, later tickets). The defensive copy runs in both the
    /// backing-field initializer and the <c>init</c> accessor so record
    /// <c>with</c> expressions cannot bypass it.
    /// </remarks>
    public sealed record PortConstraints(
        IReadOnlyList<string>? Enum,
        string? Pattern,
        double? Min,
        double? Max,
        int? MaxLength,
        bool ConstantOnly = false)
    {
        private readonly IReadOnlyList<string>? _enum = CheckEnum(Enum);

        /// <summary>The allowed values, or null when unconstrained.</summary>
        public IReadOnlyList<string>? Enum
        {
            get => _enum;
            init => _enum = CheckEnum(value);
        }

        private static IReadOnlyList<string>? CheckEnum(IReadOnlyList<string>? values)
        {
            if (values is null)
            {
                return null;
            }
            var copy = ReadOnlyCollections.CopyList(values);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Enum constraint values must not be null.", nameof(values));
                }
            }
            return copy;
        }
    }
}
