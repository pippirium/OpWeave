#nullable enable

using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Weak fingerprint of one used Operation (SPEC §C0.2 weak, §B0.2 item 5):
    /// the registered <c>id</c>/<c>version</c>, <c>descriptorHash</c> (SHA-256
    /// of the Descriptor's canonical JSON, SPEC §C3 rule 6),
    /// <c>implementationHash</c> (SHA-256 of the compiled bytes of the
    /// defining assembly) and the Core public API version string, embedded
    /// verbatim.
    /// </summary>
    /// <remarks>
    /// Instances feed the planHash <c>usedOperationFingerprints</c> input and
    /// the §F5 registry comparison of a recorded Plan
    /// (<c>descriptorHash</c>+<c>implementationHash</c>, SPEC §D0.1/§F5 item
    /// 3). The record is immutable and compares by value so a Registry
    /// rebuild over unchanged content reproduces an equal fingerprint.
    /// </remarks>
    public sealed record OperationFingerprint(
        string Id,
        int Version,
        string DescriptorHash,
        string ImplementationHash,
        string CoreApiVersion)
    {
        /// <summary>
        /// The canonical-JSON form consumed by the planHash fingerprint
        /// arrays (SPEC §0.6 SG-12): <c>id</c>, <c>version</c>,
        /// <c>descriptorHash</c>, <c>implementationHash</c>,
        /// <c>coreApiVersion</c>.
        /// </summary>
        public JsonValue ToJsonValue() => JsonValue.From(new JObject
        {
            ["id"] = Id,
            ["version"] = Version,
            ["descriptorHash"] = DescriptorHash,
            ["implementationHash"] = ImplementationHash,
            ["coreApiVersion"] = CoreApiVersion,
        });
    }
}
