#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Compiler;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] fixtures for the injected <see cref="IPlanProvenance"/> source
    /// (SPEC §0.6 SG-11): a synthetic weak provider answering fixed
    /// environment identifiers and per-type fingerprint atoms so planHash is
    /// reproducible without an Editor, plus mutable variants for the
    /// change-detection and IntegrityLevel=full cases. The Unity producer is
    /// T-W2.2-02/T-W2.2-06's.
    /// </summary>
    internal static class PlanProvenanceFixtures
    {
        /// <summary>The fixed synthetic SG-11 environment the fixtures share.</summary>
        internal static readonly PlanEnvironment Environment = new PlanEnvironment(
            "6000.0.85f1",
            "Windows",
            "StandaloneWindows64",
            "Mono2x",
            "NET_Standard",
            new[] { "DEBUG", "TRACE", "UNITY_EDITOR" });

        /// <summary>
        /// A deterministic provenance answer: the environment above, and per
        /// type the synthetic atoms "provider.&lt;hash of typeId&gt;" naming —
        /// constant per Type ID so the same registry yields the same planHash.
        /// </summary>
        internal sealed class Stub : IPlanProvenance
        {
            internal Result<PlanEnvironment> EnvironmentResult =
                Result<PlanEnvironment>.Success(PlanProvenanceFixtures.Environment);

            internal Func<TypeDefinition, Result<PlanTypeFingerprint>>? TypeFingerprintFn { get; set; }
            internal Func<TypeDefinition, Result<PlanCodecIdentity>>? CodecIdentityFn { get; set; }

            public IntegrityLevel IntegrityLevel { get; set; } = IntegrityLevel.Weak;

            public Result<PlanEnvironment> Environment => EnvironmentResult;

            public Result<PlanTypeFingerprint> TypeFingerprintFor(TypeDefinition definition) =>
                (TypeFingerprintFn ?? DefaultTypeFingerprint)(definition);

            public Result<PlanCodecIdentity> CodecIdentityFor(TypeDefinition definition) =>
                (CodecIdentityFn ?? DefaultCodecIdentity)(definition);

            private static Result<PlanTypeFingerprint> DefaultTypeFingerprint(
                TypeDefinition definition)
            {
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var digest = sha256.ComputeHash(
                    System.Text.Encoding.UTF8.GetBytes(definition.Id));
                var hex = BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
                return Result<PlanTypeFingerprint>.Success(new PlanTypeFingerprint(
                    "test.provider",
                    new string('1', 64),
                    "Test.Types",
                    hex));
            }

            private static Result<PlanCodecIdentity> DefaultCodecIdentity(
                TypeDefinition definition) =>
                Result<PlanCodecIdentity>.Success(new PlanCodecIdentity(
                    "test.provider",
                    "Test.Codecs." + definition.Id.Replace("<", "Of").Replace(">", string.Empty),
                    "Test.Codecs",
                    new string('2', 64)));
        }
    }
}
