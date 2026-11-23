#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The immutable allowlist built from a settings file's
    /// <c>allowedPackageSources</c> entries (SPEC §0.6 SG-19, §H4.1): it answers
    /// "is this <c>unity.package.add</c> source allowed" for the static D6 check
    /// and for the re-check immediately before the side effect (§G8 item 6,
    /// §0.6). A credential-bearing <c>id</c> is OW-OP-006 (§G8.1), an
    /// unparseable or unmatched source is OW-PERM-001 — both decided before a
    /// Plan exists.
    /// </summary>
    /// <remarks>
    /// The diagnostics carry only fixed English text plus the caller-supplied
    /// location: the candidate string itself is never copied into message, hint
    /// or any other field (SPEC §G8.1 "出力しない"), so a secret inside a
    /// rejected URL cannot leak through this type.
    /// </remarks>
    public sealed class PackageSourcePolicy
    {
        private const string CredentialMessage =
            "The package source contains embedded credentials.";
        private const string UnrecognizedMessage =
            "The package source is not a recognized registry name or Git URL.";
        private const string NotAllowedMessage =
            "The package source is not allowed by the allowedPackageSources setting.";
        private const string EntryInvalidMessage = "Settings key has an invalid value.";
        private const string EntryInvalidHint =
            "allowedPackageSources entries must match the closed source grammar (registry, git https or git ssh).";

        private readonly IReadOnlyList<PackageSourceRule> _rules;

        private PackageSourcePolicy(IReadOnlyList<PackageSourceRule> rules)
        {
            _rules = rules;
        }

        /// <summary>The validated entries, in settings order.</summary>
        public IReadOnlyList<PackageSourceRule> Rules => _rules;

        /// <summary>
        /// Grammar-checks every entry of <paramref name="allowedSources"/> and
        /// builds the policy. Any entry outside the SG-19 grammar fails the whole
        /// result with one OW-CFG-002 diagnostic per bad element at
        /// <c>$.allowedPackageSources[i]</c> — the settings-error state, never a
        /// silently emptied allowlist (SPEC §H4.1).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="allowedSources"/> is null.</exception>
        public static Result<PackageSourcePolicy> Create(IEnumerable<string> allowedSources)
        {
            if (allowedSources is null)
            {
                throw new ArgumentNullException(nameof(allowedSources));
            }
            var rules = new List<PackageSourceRule>();
            var diagnostics = new List<Diagnostic>();
            var index = 0;
            foreach (var entry in allowedSources)
            {
                if (entry is null || !PackageSourceRule.TryParse(entry, out var rule))
                {
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_CFG_002,
                        EntryInvalidMessage,
                        jsonPath: "$.allowedPackageSources[" +
                            index.ToString(CultureInfo.InvariantCulture) + "]",
                        hint: EntryInvalidHint));
                }
                else
                {
                    rules.Add(rule!);
                }
                index++;
            }
            if (diagnostics.Count > 0)
            {
                return Result<PackageSourcePolicy>.Failure(diagnostics);
            }
            return Result<PackageSourcePolicy>.Success(
                new PackageSourcePolicy(ReadOnlyCollections.CopyList(rules)));
        }

        /// <summary>
        /// The policy for already-loaded <paramref name="settings"/>. The loader
        /// grammar-checked <see cref="OpWeaveSettings.AllowedPackageSources"/>, so
        /// a rebuild failure here is a programming error, not data.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="settings"/> carries entries the SG-19 grammar rejects.
        /// </exception>
        public static PackageSourcePolicy For(OpWeaveSettings settings)
        {
            if (settings is null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            var result = Create(settings.AllowedPackageSources);
            if (!result.Ok)
            {
                throw new InvalidOperationException(
                    "OpWeaveSettings.AllowedPackageSources failed the SG-19 grammar; " +
                    "only loader-validated settings may reach the policy.");
            }
            return result.Value!;
        }

        /// <summary>
        /// The allowlist decision for a constant <c>unity.package.add</c>
        /// <paramref name="packageId"/>: <c>null</c> when a rule allows it, else
        /// the rejection diagnostic — OW-OP-006 for the §G8.1 credential forms
        /// (checked first, so a credential URL is never reported as a mere
        /// allowlist miss) and OW-PERM-001 for unrecognizable or unmatched
        /// sources. The candidate text is never echoed into the diagnostic.
        /// </summary>
        public Diagnostic? CheckPackageId(
            string? packageId,
            string? jsonPath = null,
            string? nodeId = null,
            string? port = null)
        {
            if (PackageIdSource.ContainsCredentials(packageId))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_OP_006, CredentialMessage,
                    nodeId: nodeId, port: port, jsonPath: jsonPath);
            }
            if (!PackageIdSource.TryParse(packageId, out var source))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_PERM_001, UnrecognizedMessage,
                    nodeId: nodeId, port: port, jsonPath: jsonPath);
            }
            if (!Allows(source!))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_PERM_001, NotAllowedMessage,
                    nodeId: nodeId, port: port, jsonPath: jsonPath);
            }
            return null;
        }

        /// <summary>True when any rule allows <paramref name="source"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        public bool Allows(PackageIdSource source)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            foreach (var rule in _rules)
            {
                var allowed = source.Kind == PackageIdKind.Registry
                    ? rule.AllowsRegistryName(source.PackageName!)
                    : rule.AllowsGitOrigin(source.NormalizedOrigin!);
                if (allowed)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
