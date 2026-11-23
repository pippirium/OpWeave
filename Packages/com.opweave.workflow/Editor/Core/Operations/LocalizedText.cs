#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// Localized display name, summary and search keywords of an Operation for
    /// one language (SPEC §C1 item 1, R8). The English text lives on the
    /// <see cref="OperationDescriptor"/> itself; this record carries a
    /// translation such as <c>"ja"</c>. Each check runs in both the
    /// backing-field initializer and the <c>init</c> accessor so record
    /// <c>with</c> expressions cannot bypass it.
    /// </summary>
    public sealed record LocalizedText(
        string DisplayName,
        string Summary,
        IReadOnlyList<string> Keywords)
    {
        private readonly string _displayName =
            DisplayName ?? throw new ArgumentNullException(nameof(DisplayName));
        private readonly string _summary =
            Summary ?? throw new ArgumentNullException(nameof(Summary));
        private readonly IReadOnlyList<string> _keywords = CheckKeywords(Keywords);

        /// <summary>The localized display name.</summary>
        public string DisplayName
        {
            get => _displayName;
            init => _displayName =
                value ?? throw new ArgumentNullException(nameof(DisplayName));
        }

        /// <summary>The localized summary.</summary>
        public string Summary
        {
            get => _summary;
            init => _summary =
                value ?? throw new ArgumentNullException(nameof(Summary));
        }

        /// <summary>The localized search keywords.</summary>
        public IReadOnlyList<string> Keywords
        {
            get => _keywords;
            init => _keywords = CheckKeywords(value);
        }

        private static IReadOnlyList<string> CheckKeywords(IReadOnlyList<string> keywords)
        {
            var copy = ReadOnlyCollections.CopyList(keywords);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Localized keywords must not contain null.", nameof(keywords));
                }
            }
            return copy;
        }
    }
}
