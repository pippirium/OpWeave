#nullable enable

using OpWeave.Core;
using OpWeave.UnityAdapter;

namespace OpWeave.Testing
{
    /// <summary>
    /// The Type Registry snapshot a harness run resolves its ports against.
    /// </summary>
    /// <remarks>
    /// SPEC §0.5 keeps the published <c>Registry</c> behind the Runner host and
    /// out of any test-only assembly's reach, and a contract test must not
    /// inherit whatever the host happened to publish — it needs the standard
    /// type set deterministically, so the harness builds its own snapshot over
    /// the same two providers with the same §C3 step-4 class-relation provider
    /// the Editor's registry build uses. Rejected definitions are not reported
    /// here: the harness only asks a type id it already found on a Descriptor,
    /// and the §C3 registration is what reports a defective provider.
    /// </remarks>
    internal static class HarnessTypes
    {
        private static readonly ITypeRegistry s_registry = Build();

        /// <summary>The built snapshot; created on first use.</summary>
        internal static ITypeRegistry Registry => s_registry;

        private static ITypeRegistry Build()
        {
            return TypeRegistryBuilder.Build(
                new[]
                {
                    new TypeProviderCandidate(
                        CoreTypeProvider.ProviderId,
                        new CoreTypeProvider(),
                        OperationOrigin.Standard),
                    new TypeProviderCandidate(
                        UnityTypeProvider.ProviderId,
                        new UnityTypeProvider(),
                        OperationOrigin.Standard),
                },
                new UnityClassRelationProvider()).Registry;
        }
    }
}
