#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// One input or output port contract of an Operation (SPEC §C1): name, Type
    /// ID, whether a value is required, an optional constant default, a
    /// description, optional <see cref="PortConstraints"/> and the
    /// <see cref="Nullable"/> flag (default <c>false</c>) that admits explicit
    /// JSON null values.
    /// </summary>
    /// <remarks>
    /// <see cref="Default"/> is a <see cref="JsonValue"/> when a default exists;
    /// a <c>null</c> reference means "no default", which is distinct from a
    /// <see cref="JsonValue"/> holding an explicit JSON <c>null</c> default —
    /// the two serialize to different bytes in the descriptorHash input
    /// (SPEC §0.6 SG-12). Each check runs in both the backing-field
    /// initializer and the <c>init</c> accessor so record <c>with</c>
    /// expressions cannot bypass it.
    /// </remarks>
    public sealed record PortDescriptor(
        string Name,
        string Type,
        bool Required,
        JsonValue? Default,
        string Description,
        PortConstraints? Constraints,
        bool Nullable = false)
    {
        private readonly string _name = CheckName(Name);
        private readonly string _type = CheckType(Type);
        private readonly string _description =
            Description ?? throw new ArgumentNullException(nameof(Description));

        /// <summary>The port name.</summary>
        public string Name
        {
            get => _name;
            init => _name = CheckName(value);
        }

        /// <summary>The port's Type ID (SPEC §B1).</summary>
        public string Type
        {
            get => _type;
            init => _type = CheckType(value);
        }

        /// <summary>The port description.</summary>
        public string Description
        {
            get => _description;
            init => _description =
                value ?? throw new ArgumentNullException(nameof(Description));
        }

        private static string CheckName(string name) =>
            string.IsNullOrEmpty(name)
                ? throw new ArgumentException(
                    "Port name must not be null or empty.", nameof(Name))
                : name;

        private static string CheckType(string type) =>
            string.IsNullOrEmpty(type)
                ? throw new ArgumentException(
                    "Port type must not be null or empty.", nameof(Type))
                : type;
    }
}
