#nullable enable

using System;
using System.Text;

namespace OpWeave.Core
{
    /// <summary>
    /// JSON naming rules of SPEC §0.4: members are camelCase and enum values are snake_case.
    /// Inputs are expected to be PascalCase CLR identifiers.
    /// </summary>
    public static class JsonNaming
    {
        /// <summary>
        /// "FormatVersion" → "formatVersion". A leading acronym run collapses
        /// ("URLValue" → "urlValue"); input that is already camelCase is returned unchanged.
        /// </summary>
        public static string ToCamelCase(string name)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }
            if (name.Length == 0 || !char.IsUpper(name[0]))
            {
                return name;
            }
            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (i > 0 && i + 1 < chars.Length && !char.IsUpper(chars[i + 1]))
                {
                    break;
                }
                chars[i] = char.ToLowerInvariant(chars[i]);
            }
            return new string(chars);
        }

        /// <summary>
        /// "FromStep" → "from_step". Acronyms stay together ("IOOperation" → "io_operation")
        /// and a word preceded by a digit is split ("Version2Value" → "version2_value").
        /// </summary>
        public static string ToSnakeCase(string name)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }
            var builder = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0 &&
                        (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) ||
                         (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    {
                        builder.Append('_');
                    }
                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }
    }
}
