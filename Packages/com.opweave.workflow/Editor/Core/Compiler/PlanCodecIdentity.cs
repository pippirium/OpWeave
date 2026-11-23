#nullable enable

using System;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The provider-supplied identity atoms of one used Codec (SPEC §0.6
    /// SG-11): the registering Provider ID, the codec's namespace-qualified
    /// CLR <c>FullName</c> (never an AssemblyQualifiedName's version data),
    /// and the compiled-bytes identity of the codec's defining assembly —
    /// which may differ from the TypeProvider's assembly. The Build Plan
    /// stage embeds them verbatim with the <c>typeId</c> the codec serves and
    /// <c>coreApiVersion</c> into the SG-11 Codec identity object whose
    /// SHA-256 is the Codec ID. An unobtainable value is a failed
    /// <see cref="Result{T}"/>, never a guess or empty string.
    /// </summary>
    public sealed record PlanCodecIdentity(
        string ProviderId,
        string CodecTypeFullName,
        string DefiningAssemblyName,
        string DefiningAssemblySha256)
    {
        /// <summary>The Registry Provider ID (<c>@builtin</c> for the standard set).</summary>
        public string ProviderId { get; init; } = Check(ProviderId, nameof(ProviderId));

        /// <summary>The codec's namespace-qualified CLR FullName.</summary>
        public string CodecTypeFullName { get; init; } =
            Check(CodecTypeFullName, nameof(CodecTypeFullName));

        /// <summary>The codec's defining assembly simple name.</summary>
        public string DefiningAssemblyName { get; init; } =
            Check(DefiningAssemblyName, nameof(DefiningAssemblyName));

        /// <summary>Lowercase-hex SHA-256 of the codec's compiled assembly bytes.</summary>
        public string DefiningAssemblySha256 { get; init; } =
            Check(DefiningAssemblySha256, nameof(DefiningAssemblySha256));

        private static string Check(string value, string name) =>
            string.IsNullOrEmpty(value)
                ? throw new ArgumentException(
                    "A codec identity field must not be null or empty; " +
                    "return a failed Result instead (SPEC §0.6 SG-11).", name)
                : value;
    }
}
