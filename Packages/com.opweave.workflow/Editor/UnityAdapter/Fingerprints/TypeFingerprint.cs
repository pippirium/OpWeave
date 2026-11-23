#nullable enable

using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Weak fingerprint of one used Type (SPEC §C0.2 weak, §B0.2 item 5): the
    /// registered Type ID and the Provider ID that registered it, the
    /// SHA-256 of the <c>TypeDefinition</c>'s canonical JSON
    /// (<c>typeDefinitionHash</c>, the Descriptor analogue for types), the
    /// compiled-bytes identity of the provider's defining assembly, and the
    /// Core public API version string.
    /// </summary>
    /// <remarks>
    /// Instances feed the planHash <c>usedTypeFingerprints</c> input. The
    /// Codec identity object of SPEC §0.6 SG-11 is assembled separately;
    /// <see cref="DefiningAssemblyFingerprint"/> carries the two
    /// <c>definingAssembly*</c> fields it shares with this shape.
    /// </remarks>
    public sealed record TypeFingerprint(
        string TypeId,
        string ProviderId,
        string TypeDefinitionHash,
        DefiningAssemblyFingerprint Assembly,
        string CoreApiVersion)
    {
        /// <summary>
        /// The canonical-JSON form consumed by the planHash fingerprint
        /// arrays: <c>typeId</c>, <c>providerId</c>,
        /// <c>typeDefinitionHash</c>, <c>definingAssemblyName</c>,
        /// <c>definingAssemblySha256</c>, <c>coreApiVersion</c>.
        /// </summary>
        public JsonValue ToJsonValue() => JsonValue.From(new JObject
        {
            ["typeId"] = TypeId,
            ["providerId"] = ProviderId,
            ["typeDefinitionHash"] = TypeDefinitionHash,
            ["definingAssemblyName"] = Assembly.DefiningAssemblyName,
            ["definingAssemblySha256"] = Assembly.DefiningAssemblySha256,
            ["coreApiVersion"] = CoreApiVersion,
        });
    }
}
