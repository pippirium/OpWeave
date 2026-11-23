#nullable enable

using System;

namespace OpWeave.Core
{
    /// <summary>
    /// A step input port's resolved value at the boundary where "not supplied"
    /// must stay distinct from an explicit JSON <c>null</c> (SPEC §A0.1):
    /// <see cref="IsPresent"/> is <c>false</c> for a <see cref="Compiler.Missing"/>
    /// source and <c>true</c> for every supplied value — including one whose
    /// <see cref="Value"/> is the <see cref="JsonValue"/> of literal
    /// <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The struct is the value-level view of the missing-vs-null contract: a
    /// <c>default(InputValue)</c> is <see cref="Missing"/>, so nothing can be
    /// "present but unbacked". <see cref="Value"/> is <c>null</c> exactly when
    /// <see cref="IsPresent"/> is <c>false</c>; consumers branch on
    /// <see cref="IsPresent"/> (StepContext's <c>IsPresent(port)</c>, SPEC §0.6
    /// SG-06) instead of testing for <c>null</c> value text.
    /// </remarks>
    public readonly struct InputValue : IEquatable<InputValue>
    {
        private readonly JsonValue? _value;

        private InputValue(JsonValue value)
        {
            _value = value;
        }

        /// <summary>The absent value: <see cref="IsPresent"/> is <c>false</c>.</summary>
        public static InputValue Missing => default;

        /// <summary>True when a value was supplied — even a literal JSON <c>null</c>.</summary>
        public bool IsPresent => _value is not null;

        /// <summary>
        /// The supplied value; <c>null</c> exactly when <see cref="IsPresent"/> is
        /// <c>false</c>. A <see cref="JsonValue"/> whose canonical text is
        /// <c>"null"</c> is a present value, not <see cref="Missing"/>.
        /// </summary>
        public JsonValue? Value => _value;

        /// <summary>
        /// Wraps a supplied <paramref name="value"/>. Explicit JSON null stays a
        /// present value (SPEC §A0.1).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public static InputValue Present(JsonValue value) =>
            new InputValue(value ?? throw new ArgumentNullException(nameof(value)));

        /// <summary>Equality: both absent, or both present with equal JsonValues.</summary>
        public bool Equals(InputValue other) =>
            _value is null ? other._value is null : _value.Equals(other._value);

        public override bool Equals(object? obj) => obj is InputValue other && Equals(other);

        public override int GetHashCode() => _value?.GetHashCode() ?? 0;

        public static bool operator ==(InputValue left, InputValue right) => left.Equals(right);

        public static bool operator !=(InputValue left, InputValue right) => !left.Equals(right);
    }
}
