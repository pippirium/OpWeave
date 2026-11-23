#nullable enable

using System;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The provider-supplied identity atoms of one used Type (SPEC §C0.2 weak,
    /// §0.6 SG-11): the registering Provider ID, the
    /// <c>typeDefinitionHash</c> of the TypeDefinition's canonical JSON, and
    /// the compiled-bytes identity of the provider's defining assembly. The
    /// Build Plan stage embeds them verbatim with the registered
    /// <c>typeId</c> and <c>coreApiVersion</c> into the planHash
    /// <c>usedTypeFingerprints</c> object. An unobtainable value is a failed
    /// <see cref="Result{T}"/>, never a guess or empty string (SPEC §0.6 SG-11).
    /// </summary>
    public sealed record PlanTypeFingerprint(
        string ProviderId,
        string TypeDefinitionHash,
        string DefiningAssemblyName,
        string DefiningAssemblySha256)
    {
        /// <summary>The Registry Provider ID (<c>@builtin</c> for the standard set).</summary>
        public string ProviderId { get; init; } = Check(ProviderId, nameof(ProviderId));

        /// <summary>Lowercase-hex SHA-256 of the TypeDefinition's canonical JSON.</summary>
        public string TypeDefinitionHash { get; init; } =
            Check(TypeDefinitionHash, nameof(TypeDefinitionHash));

        /// <summary>The provider's defining assembly simple name.</summary>
        public string DefiningAssemblyName { get; init; } =
            Check(DefiningAssemblyName, nameof(DefiningAssemblyName));

        /// <summary>Lowercase-hex SHA-256 of the provider's compiled assembly bytes.</summary>
        public string DefiningAssemblySha256 { get; init; } =
            Check(DefiningAssemblySha256, nameof(DefiningAssemblySha256));

        private static string Check(string value, string name) =>
            string.IsNullOrEmpty(value)
                ? throw new ArgumentException(
                    "A fingerprint identifier must not be null or empty; " +
                    "return a failed Result instead (SPEC §0.6 SG-11).", name)
                : value;
    }
}
