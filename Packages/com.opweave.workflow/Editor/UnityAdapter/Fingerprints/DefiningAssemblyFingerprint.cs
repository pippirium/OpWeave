#nullable enable

using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Compiled-bytes identity of one defining assembly (SPEC §0.1 F-09,
    /// §C3 rule 6): the assembly's name and the lowercase-hex SHA-256 of the
    /// exact DLL bytes it was compiled into. Source text and the assembly
    /// MVID never enter this record (SPEC §C3 rule 6, §X.8).
    /// </summary>
    /// <remarks>
    /// This is the assembly slice every weak fingerprint shares
    /// (SPEC §C0.2 weak): for an Operation it is the
    /// <c>implementationHash</c> input, for a Codec it supplies the
    /// <c>definingAssemblyName</c>/<c>definingAssemblySha256</c> fields of the
    /// SG-11 Codec identity object (built by a later stage), for a Type it is
    /// the defining assembly of the TypeProvider that registered it.
    /// </remarks>
    public sealed record DefiningAssemblyFingerprint(
        string DefiningAssemblyName,
        string DefiningAssemblySha256)
    {
        /// <summary>
        /// The canonical-JSON form with the fixed SG-11 field names
        /// <c>definingAssemblyName</c> and <c>definingAssemblySha256</c>.
        /// </summary>
        public JsonValue ToJsonValue() => JsonValue.From(new JObject
        {
            ["definingAssemblyName"] = DefiningAssemblyName,
            ["definingAssemblySha256"] = DefiningAssemblySha256,
        });
    }
}
