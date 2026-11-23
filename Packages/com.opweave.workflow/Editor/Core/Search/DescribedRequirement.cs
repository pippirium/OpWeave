#nullable enable

using System;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// One declared precondition of a described Operation (SPEC §C8): the
    /// <see cref="Operations.Requirement"/> itself plus its fixed-English
    /// description — the 前提条件の説明 §C8 lists beside the schemas.
    /// </summary>
    public sealed record DescribedRequirement(Requirement Requirement, string Description)
    {
        private readonly Requirement _requirement =
            Requirement ?? throw new ArgumentNullException(nameof(Requirement));
        private readonly string _description = CheckDescription(Description);

        /// <summary>The declared requirement.</summary>
        public Requirement Requirement
        {
            get => _requirement;
            init => _requirement =
                value ?? throw new ArgumentNullException(nameof(Requirement));
        }

        /// <summary>The fixed-English description of what the requirement asks.</summary>
        public string Description
        {
            get => _description;
            init => _description = CheckDescription(value);
        }

        private static string CheckDescription(string description) =>
            string.IsNullOrEmpty(description)
                ? throw new ArgumentException(
                    "A requirement description must not be null or empty.",
                    nameof(Description))
                : description;
    }
}
