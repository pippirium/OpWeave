#nullable enable

using System;

namespace OpWeave.Core
{
    /// <summary>
    /// One permitted, already instantiated <see cref="ITypeProvider"/> together with
    /// the Provider ID and origin assigned by the registration stage
    /// (SPEC §C3 steps 3–4: providers are generated in deterministic order after
    /// whitelist/permission checks; instantiation failures are reported by the
    /// caller as OW-OP-019 and never reach this record).
    /// </summary>
    /// <remarks>
    /// <see cref="Origin"/> reuses the §C3 <see cref="OperationOrigin"/> axis:
    /// <see cref="OperationOrigin.Standard"/> marks providers shipped inside the
    /// <c>com.opweave.workflow</c> package, the only ones allowed to register the
    /// reserved namespaces <c>core.*</c>, <c>unity.*</c> and <c>opweave.*</c>.
    /// The built-in provider uses the fixed Provider ID <c>@builtin</c>
    /// (SPEC §0.6 SG-11).
    /// </remarks>
    public sealed record TypeProviderCandidate(string ProviderId, ITypeProvider Provider, OperationOrigin Origin)
    {
        /// <summary>
        /// The Provider ID assigned by the registration stage
        /// (<c>opweave.extension.json</c> <c>typeProviders[].id</c>, or
        /// <c>@builtin</c> for the standard provider).
        /// </summary>
        public string ProviderId { get; init; } =
            string.IsNullOrEmpty(ProviderId)
                ? throw new ArgumentException(
                    "Provider ID must not be null or empty.", nameof(ProviderId))
                : ProviderId;

        /// <summary>The permitted provider instance.</summary>
        public ITypeProvider Provider { get; init; } =
            Provider ?? throw new ArgumentNullException(nameof(Provider));
    }
}
