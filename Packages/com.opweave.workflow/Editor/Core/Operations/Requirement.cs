#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// An environmental precondition an Operation declares for static
    /// permission/precondition checks (SPEC §C1, §D6, §G1), e.g.
    /// <c>"edit_mode"</c>, <c>"scene_loaded"</c>, <c>"path_writable"</c> or
    /// <c>"type_exists"</c>. <see cref="Port"/> optionally names the port the
    /// requirement applies to. The check runs in both the backing-field
    /// initializer and the <c>init</c> accessor so record <c>with</c>
    /// expressions cannot bypass it.
    /// </summary>
    public sealed record Requirement(string Kind, string? Port = null)
    {
        private readonly string _kind = CheckKind(Kind);

        /// <summary>The requirement kind.</summary>
        public string Kind
        {
            get => _kind;
            init => _kind = CheckKind(value);
        }

        private static string CheckKind(string kind) =>
            string.IsNullOrEmpty(kind)
                ? throw new ArgumentException(
                    "Requirement kind must not be null or empty.", nameof(Kind))
                : kind;
    }
}
