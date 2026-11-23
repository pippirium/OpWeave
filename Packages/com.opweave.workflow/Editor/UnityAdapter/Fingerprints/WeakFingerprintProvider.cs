#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Weak implementation fingerprints (SPEC §C0.2 weak, §C3 rule 6, §0.1
    /// F-09): per used Operation, Type and Codec, the identity is bound to
    /// the SHA-256 of the <em>compiled bytes</em> of its defining assembly —
    /// never to source-file content or the assembly MVID. Consumed by the
    /// Registry fingerprint stage and by the planHash fingerprint provider
    /// for used definitions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>implementationHash</c> is computed by hashing the byte stream of
    /// the DLL named by <see cref="EditorAssemblyProvenance.CompiledAssemblyPath"/>
    /// on every call; no caching, so a Registry rebuild over a recompiled
    /// assembly observes the new bytes. An unresolvable or unreadable
    /// defining assembly fails with an OW-OP-017 diagnostic — the record the
    /// registration stage uses to refuse the candidate (SPEC §C3 rule 6,
    /// §C0.2 weak); no identifier is guessed or defaulted (SPEC §0.6 SG-11).
    /// </para>
    /// <para>
    /// The remaining weak-fingerprint inputs arrive already identified by
    /// the stages that own them: the Descriptor / TypeDefinition as the
    /// canonical-JSON <see cref="JsonValue"/> whose
    /// <see cref="JsonValue.Sha256"/> is the <c>descriptorHash</c> /
    /// <c>typeDefinitionHash</c> (SPEC §C3 rule 6 applied to the contract
    /// document), and <paramref name="coreApiVersion" /> as the
    /// <c>CoreApi.Version</c> string embedded verbatim (SPEC §B0.2 item 5).
    /// The SG-11 Codec identity object and the six Unity environment keys
    /// are assembled by a later stage on top of
    /// <see cref="DefiningAssemblyFingerprint"/>.
    /// </para>
    /// </remarks>
    public static class WeakFingerprintProvider
    {
        /// <summary>
        /// Hashes the compiled bytes of an already resolved defining
        /// assembly (SPEC §G0 → §C3 rule 6). Fails OW-OP-017 when the path is
        /// empty or the bytes cannot be read.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="provenance"/> is null.</exception>
        public static Result<DefiningAssemblyFingerprint> ForProvenance(EditorAssemblyProvenance provenance)
        {
            if (provenance is null)
            {
                throw new ArgumentNullException(nameof(provenance));
            }

            var path = provenance.CompiledAssemblyPath;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is NotSupportedException || ex is ArgumentException ||
                ex is System.Security.SecurityException)
            {
                return Result<DefiningAssemblyFingerprint>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_017,
                    "Defining assembly's compiled bytes could not be read.",
                    hint: $"{provenance.AssemblyName} ({path})"));
            }

            return Result<DefiningAssemblyFingerprint>.Success(
                new DefiningAssemblyFingerprint(provenance.AssemblyName, Sha256Hex(bytes)));
        }

        /// <summary>
        /// Resolves <paramref name="type"/>'s defining assembly
        /// (<see cref="EditorAssemblyResolver"/>) and hashes its compiled
        /// bytes. Resolution failures propagate as the resolver's OW-OP-017
        /// diagnostics. For a Codec this yields the
        /// <c>definingAssemblyName</c>/<c>definingAssemblySha256</c> pair of
        /// the SG-11 identity object.
        /// </summary>
        /// <param name="assemblyResolver">
        /// The defining-assembly resolution to apply — the live
        /// <see cref="EditorAssemblyResolver.ResolveForType"/> when omitted;
        /// tests inject it to force the OW-OP-017 outcome a foreign or
        /// byte-loaded DLL produces.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
        public static Result<DefiningAssemblyFingerprint> ForDefiningType(
            Type type,
            Func<Type, Result<EditorAssemblyProvenance>>? assemblyResolver = null)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            var resolved = assemblyResolver is null
                ? EditorAssemblyResolver.ResolveForType(type)
                : assemblyResolver(type);
            if (!resolved.Ok)
            {
                return Result<DefiningAssemblyFingerprint>.Failure(resolved.Diagnostics);
            }
            return ForProvenance(resolved.Value!);
        }

        /// <summary>
        /// Weak fingerprint of one used Operation
        /// (SPEC §C0.2 weak, §C3 rule 6): <paramref name="descriptorCanonical"/>
        /// hashed into <c>descriptorHash</c>, the compiled bytes of
        /// <paramref name="implementationType"/>'s defining assembly into
        /// <c>implementationHash</c>, plus <paramref name="coreApiVersion"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="operationId"/> or <paramref name="coreApiVersion"/> is empty, or
        /// <paramref name="version"/> is less than 1 — identifiers are never
        /// guessed (SPEC §0.6 SG-11).
        /// </exception>
        public static Result<OperationFingerprint> ForOperation(
            string operationId,
            int version,
            JsonValue descriptorCanonical,
            Type implementationType,
            string coreApiVersion)
        {
            RequireNonEmpty(operationId, nameof(operationId));
            if (version < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(version), version,
                    "Operation version must be >= 1.");
            }
            if (descriptorCanonical is null)
            {
                throw new ArgumentNullException(nameof(descriptorCanonical));
            }
            RequireNonEmpty(coreApiVersion, nameof(coreApiVersion));

            var assembly = ForDefiningType(implementationType);
            if (!assembly.Ok)
            {
                return Result<OperationFingerprint>.Failure(assembly.Diagnostics);
            }
            return Result<OperationFingerprint>.Success(new OperationFingerprint(
                operationId, version, descriptorCanonical.Sha256,
                assembly.Value!.DefiningAssemblySha256, coreApiVersion));
        }

        /// <summary>
        /// Weak fingerprint of one used Type (SPEC §C0.2 weak): the
        /// <paramref name="typeDefinitionCanonical"/> hash, and the compiled
        /// bytes of the defining assembly of the TypeProvider class
        /// <paramref name="providerType"/> that registered it, plus
        /// <paramref name="coreApiVersion"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="typeId"/>, <paramref name="providerId"/> or
        /// <paramref name="coreApiVersion"/> is empty.
        /// </exception>
        public static Result<TypeFingerprint> ForType(
            string typeId,
            string providerId,
            JsonValue typeDefinitionCanonical,
            Type providerType,
            string coreApiVersion)
        {
            RequireNonEmpty(typeId, nameof(typeId));
            RequireNonEmpty(providerId, nameof(providerId));
            if (typeDefinitionCanonical is null)
            {
                throw new ArgumentNullException(nameof(typeDefinitionCanonical));
            }
            RequireNonEmpty(coreApiVersion, nameof(coreApiVersion));

            var assembly = ForDefiningType(providerType);
            if (!assembly.Ok)
            {
                return Result<TypeFingerprint>.Failure(assembly.Diagnostics);
            }
            return Result<TypeFingerprint>.Success(new TypeFingerprint(
                typeId, providerId, typeDefinitionCanonical.Sha256,
                assembly.Value!, coreApiVersion));
        }

        private static void RequireNonEmpty(string value, string paramName)
        {
            if (value is null)
            {
                throw new ArgumentNullException(paramName);
            }
            if (value.Length == 0)
            {
                throw new ArgumentException("Identifier must not be empty.", paramName);
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(bytes);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
