#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Compiler;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Unity-side producer of the Build Plan stage's planHash material
    /// (SPEC §D7 item 5, §0.6 SG-11): the production
    /// <see cref="IPlanProvenance"/> that carries the configured
    /// <see cref="IntegrityLevel"/>, an immutable snapshot of the six
    /// Unity environment identifiers, and the weak per-Type/Codec fingerprint
    /// atoms of the registered definitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Type ID → Provider binding is fixed at construction by the same
    /// §C3 step-4 acceptance pipeline the type build runs —
    /// <see cref="TypeRegistryBuilder.AcceptedBindings"/> over the same
    /// <see cref="TypeProviderCandidate"/> set (SPEC §C0.1: providers are
    /// stateless and deterministic, so re-enumerating
    /// <see cref="ITypeProvider.GetTypes"/> reproduces the registered
    /// offers). Only offers the build accepts are bound: candidates
    /// rejected before registration — duplicate Provider IDs, provider
    /// failures, defective definitions, colliding Type IDs and graph
    /// exclusions — contribute nothing, so a Type ID a rejected provider
    /// also offered still resolves to its accepted registering provider.
    /// A Type ID the build never accepted — unregistered or
    /// collision-invalidated — is never guessed: the query fails with an
    /// OW-OP-017 diagnostic, the code this layer reports for unresolvable
    /// weak-fingerprint material (SPEC §C0.2 weak, §C3), as does a
    /// defining assembly <see cref="WeakFingerprintProvider"/> cannot
    /// read. A definition whose Codec is missing answers OW-OP-016 (SPEC
    /// §C3 step 4).
    /// </para>
    /// <para>
    /// <see cref="CodecIdentityFor"/> identifies the Codec the resolved
    /// definition carries — including a <c>IClosedGenericCodec</c>-bound
    /// instance — by its CLR <c>FullName</c> (a constructed generic's
    /// arguments are dropped back to the type definition, so no
    /// AssemblyQualifiedName version data enters, SPEC §0.6 SG-11) and the
    /// compiled-bytes SHA-256 of <em>its own</em> defining assembly, which may
    /// differ from the TypeProvider's. The <c>providerId</c> is the registering
    /// Provider ID — <c>@builtin</c> for the standard set, the Registry
    /// Provider ID otherwise.
    /// </para>
    /// </remarks>
    public sealed class UnityPlanProvenance : IPlanProvenance
    {
        private readonly Result<PlanEnvironment> _environment;
        private readonly IReadOnlyDictionary<string, TypeProviderCandidate> _bindings;
        private readonly Func<Type, Result<EditorAssemblyProvenance>> _assemblyResolver;

        /// <summary>
        /// A provenance over an already captured environment snapshot.
        /// </summary>
        /// <param name="integrityLevel">
        /// The configured <see cref="OpWeave.Core.IntegrityLevel"/> — the
        /// <c>extensions.integrityLevel</c> value of the settings source
        /// (SPEC §H4.1). <see cref="IntegrityLevel.Full"/> is carried verbatim
        /// so the Build Plan stage rejects it with OW-OP-011, never downgraded.
        /// </param>
        /// <param name="environment">
        /// The immutable environment snapshot, e.g. from
        /// <see cref="UnityEnvironmentSnapshot.Capture"/>; a failure Result is
        /// carried verbatim.
        /// </param>
        /// <param name="providers">
        /// The permitted <see cref="TypeProviderCandidate"/>s of the §C3
        /// step-4 build; the shared acceptance pipeline assigns every Type
        /// ID the build accepts its registering Provider ID and provider
        /// class.
        /// </param>
        /// <param name="assemblyResolver">
        /// The defining-assembly resolution handed to
        /// <see cref="WeakFingerprintProvider"/> — the live
        /// <see cref="EditorAssemblyResolver.ResolveForType"/> when omitted;
        /// tests inject it to force the OW-OP-017 outcome a foreign or
        /// byte-loaded DLL produces.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="providers"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="providers"/> yields a null candidate, or
        /// <paramref name="environment"/> is a default (unconstructed)
        /// <see cref="Result{T}"/> that would fail the Plan without a diagnostic.
        /// </exception>
        public UnityPlanProvenance(
            IntegrityLevel integrityLevel,
            Result<PlanEnvironment> environment,
            IEnumerable<TypeProviderCandidate> providers,
            Func<Type, Result<EditorAssemblyProvenance>>? assemblyResolver = null)
        {
            if (providers is null)
            {
                throw new ArgumentNullException(nameof(providers));
            }
            if (!environment.Ok && environment.Diagnostics.Count == 0)
            {
                throw new ArgumentException(
                    "A failed environment Result must carry at least one diagnostic.",
                    nameof(environment));
            }
            IntegrityLevel = integrityLevel;
            _environment = environment;
            _assemblyResolver = assemblyResolver is null
                ? (Func<Type, Result<EditorAssemblyProvenance>>)EditorAssemblyResolver.ResolveForType
                : assemblyResolver;
            _bindings = TypeRegistryBuilder.AcceptedBindings(providers);
        }

        /// <summary>
        /// Captures the live environment identifiers through
        /// <see cref="UnityEnvironmentSnapshot"/> and constructs the
        /// production provenance.
        /// </summary>
        public static UnityPlanProvenance Capture(
            IntegrityLevel integrityLevel,
            IEnumerable<TypeProviderCandidate> providers) =>
            new UnityPlanProvenance(
                integrityLevel, UnityEnvironmentSnapshot.Capture(), providers);

        /// <inheritdoc/>
        public IntegrityLevel IntegrityLevel { get; }

        /// <inheritdoc/>
        public Result<PlanEnvironment> Environment => _environment;

        /// <inheritdoc/>
        public Result<PlanTypeFingerprint> TypeFingerprintFor(TypeDefinition definition)
        {
            if (definition is null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            if (!TryProvider(definition, out var candidate))
            {
                return Unbound<PlanTypeFingerprint>(definition);
            }

            // The provider class's defining assembly supplies the compiled-bytes
            // identity (SPEC §C0.2 weak); the definition contract's canonical
            // JSON supplies the typeDefinitionHash.
            var assembly = WeakFingerprintProvider.ForDefiningType(
                candidate.Provider.GetType(), _assemblyResolver);
            return assembly.Ok
                ? Result<PlanTypeFingerprint>.Success(new PlanTypeFingerprint(
                    candidate.ProviderId,
                    TypeDefinitionHash.Compute(definition),
                    assembly.Value!.DefiningAssemblyName,
                    assembly.Value!.DefiningAssemblySha256))
                : Result<PlanTypeFingerprint>.Failure(assembly.Diagnostics);
        }

        /// <inheritdoc/>
        public Result<PlanCodecIdentity> CodecIdentityFor(TypeDefinition definition)
        {
            if (definition is null)
            {
                throw new ArgumentNullException(nameof(definition));
            }
            if (!TryProvider(definition, out var candidate))
            {
                return Unbound<PlanCodecIdentity>(definition);
            }
            var codec = definition.Codec;
            if (codec is null)
            {
                return Result<PlanCodecIdentity>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_016,
                    $"Type '{definition.Id}' has no Codec to identify."));
            }

            var codecType = codec.GetType();
            var fullName = CodecTypeName(codecType);
            if (string.IsNullOrEmpty(fullName))
            {
                return Result<PlanCodecIdentity>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_017,
                    $"The Codec type name of type '{definition.Id}' could not be resolved."));
            }
            var assembly = WeakFingerprintProvider.ForDefiningType(codecType, _assemblyResolver);
            return assembly.Ok
                ? Result<PlanCodecIdentity>.Success(new PlanCodecIdentity(
                    candidate.ProviderId,
                    fullName!,
                    assembly.Value!.DefiningAssemblyName,
                    assembly.Value!.DefiningAssemblySha256))
                : Result<PlanCodecIdentity>.Failure(assembly.Diagnostics);
        }

        // The codec's namespace-qualified CLR FullName (SPEC §0.6 SG-11): a
        // constructed generic is reported by its generic type definition so no
        // AssemblyQualifiedName version data leaks in.
        private static string? CodecTypeName(Type codecType) =>
            codecType.IsGenericType
                ? codecType.GetGenericTypeDefinition().FullName
                : codecType.FullName;

        private bool TryProvider(TypeDefinition definition, out TypeProviderCandidate candidate)
        {
            if (_bindings.TryGetValue(definition.Id, out var bound))
            {
                candidate = bound;
                return true;
            }
            candidate = null!;
            return false;
        }

        private static Result<T> Unbound<T>(TypeDefinition definition) =>
            Result<T>.Failure(ErrorCodes.Create(
                ErrorCodes.OW_OP_017,
                $"No unique registering TypeProvider could be resolved for type '{definition.Id}'."));
    }
}
