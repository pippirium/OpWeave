#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The <c>target</c> object of a SPEC §F7 change element: what a recorded
    /// change applies to. <see cref="Kind"/> names the target category and
    /// <see cref="Path"/> its location; <see cref="Guid"/> and
    /// <see cref="GlobalId"/> carry the durable identifiers reconciliation
    /// relies on (SPEC §F5 common rules) whenever they exist.
    /// </summary>
    public sealed record ChangeTarget(
        string Kind,
        string Path,
        string? Guid = null,
        string? GlobalId = null)
    {
        private readonly string _kind =
            string.IsNullOrEmpty(Kind)
                ? throw new ArgumentException(
                    "A change target must carry a kind.", nameof(Kind))
                : Kind;
        private readonly string _path =
            Path ?? throw new ArgumentNullException(nameof(Path));

        /// <summary>The target category (for example <c>"asset"</c> or <c>"gameObject"</c>).</summary>
        public string Kind
        {
            get => _kind;
            init => _kind =
                string.IsNullOrEmpty(value)
                    ? throw new ArgumentException(
                        "A change target must carry a kind.", nameof(Kind))
                    : value;
        }

        /// <summary>The target location — an asset path or a hierarchy path.</summary>
        public string Path
        {
            get => _path;
            init => _path =
                value ?? throw new ArgumentNullException(nameof(Path));
        }
    }
}
