#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One level of the SPEC §C4 side-effect policy vocabulary shared by
    /// <c>policy.agent</c> and <c>policy.human</c> (SPEC §H4.1).
    /// </summary>
    public enum PolicyLevel
    {
        Allow,
        Ask,
        Deny,
    }

    /// <summary>
    /// The <c>extensions.discoveryMode</c> axis of SPEC §C0.2/§H4.1: how custom and
    /// external extensions become eligible for registration.
    /// </summary>
    public enum DiscoveryMode
    {
        Auto,
        Whitelist,
    }

    /// <summary>
    /// One <c>extensions.whitelist.operations</c> entry (SPEC §H4.1/§C0.2): the
    /// exact-match tuple <c>packageId + assemblyName + operationId + version</c>.
    /// <c>"@project"</c> is the reserved <see cref="PackageId"/> for assemblies that
    /// belong to no UPM package. Wildcards and empty strings never reach this record;
    /// the loader rejects them with OW-CFG-002.
    /// </summary>
    public sealed record OperationWhitelistEntry(
        string PackageId,
        string AssemblyName,
        string OperationId,
        long Version);

    /// <summary>
    /// One <c>extensions.whitelist.typeProviders</c> entry (SPEC §H4.1/§C0.2): the
    /// exact-match tuple <c>packageId + providerId</c>.
    /// </summary>
    public sealed record TypeProviderWhitelistEntry(string PackageId, string ProviderId);

    /// <summary>
    /// The immutable in-memory form of <c>ProjectSettings/OpWeaveSettings.json</c>
    /// (SPEC §H4.1): every v2 key with its documented default already applied, plus
    /// load metadata a later human save needs. Produced by
    /// <see cref="OpWeaveSettingsLoader"/> only; there is no write path (T-W3.4-04).
    /// </summary>
    /// <remarks>
    /// <see cref="AgentPolicy"/> already carries the §H4 rule that an Agent
    /// <c>code.write</c> of <c>"allow"</c> is treated as <see cref="PolicyLevel.Ask"/>;
    /// <see cref="OpWeaveSettingsLoader"/> emits the companion OW-CFG-005 warning when
    /// it reads such a file (SPEC §0.6 SG-18). The settings-error
    /// state of SPEC §H4.1 (standard Operations only, no new Runs) is not a property of
    /// this object: it is the failed <see cref="Result{T}"/> the loader returns, so an
    /// error can never be observed as a populated settings value.
    /// </remarks>
    public sealed class OpWeaveSettings
    {
        /// <summary>The current settings format version (SPEC §H4.1).</summary>
        public const int CurrentVersion = 2;

        private static readonly IReadOnlyDictionary<string, JsonValue> s_noPreserved =
            ReadOnlyCollections.CopyMap<JsonValue>(Array.Empty<KeyValuePair<string, JsonValue>>());

        /// <summary>The §C4 effect names that close the policy vocabulary.</summary>
        internal static readonly IReadOnlyList<string> KnownEffects = EffectNames.All;

        private static readonly OpWeaveSettings s_default = new OpWeaveSettings(
            agentPolicy: DefaultAgentPolicy(),
            humanPolicy: DefaultHumanPolicy(),
            destructiveRequiresAsk: true,
            discoveryMode: DiscoveryMode.Auto,
            integrityLevel: IntegrityLevel.Weak,
            whitelistedOperations: Array.Empty<OperationWhitelistEntry>(),
            whitelistedTypeProviders: Array.Empty<TypeProviderWhitelistEntry>(),
            allowedPaths: new[] { "Assets/" },
            codePaths: new[] { "Assets/Scripts/" },
            customOperationFolders: new[] { "Assets/OpWeave/Editor/Operations/" },
            allowedPackageSources: new[] { "registry:com.unity.*" },
            disabledOperations: Array.Empty<string>(),
            agentLanguage: "en",
            debugLogging: false,
            tickBudgetMs: 50,
            retainRuns: 50,
            retainMegabytes: 200,
            approvalTimeoutMinutes: 1440,
            migratedFromV1: false,
            preservedUnknownKeys: s_noPreserved);

        /// <summary>
        /// Every documented default of SPEC §H4.1, used for a missing file or a missing
        /// key. Never applied silently over an invalid value: invalid content fails the
        /// load instead.
        /// </summary>
        public static OpWeaveSettings Default => s_default;

        internal OpWeaveSettings(
            IReadOnlyDictionary<string, PolicyLevel> agentPolicy,
            IReadOnlyDictionary<string, PolicyLevel> humanPolicy,
            bool destructiveRequiresAsk,
            DiscoveryMode discoveryMode,
            IntegrityLevel integrityLevel,
            IEnumerable<OperationWhitelistEntry> whitelistedOperations,
            IEnumerable<TypeProviderWhitelistEntry> whitelistedTypeProviders,
            IEnumerable<string> allowedPaths,
            IEnumerable<string> codePaths,
            IEnumerable<string> customOperationFolders,
            IEnumerable<string> allowedPackageSources,
            IEnumerable<string> disabledOperations,
            string agentLanguage,
            bool debugLogging,
            long tickBudgetMs,
            long retainRuns,
            long retainMegabytes,
            long approvalTimeoutMinutes,
            bool migratedFromV1,
            IReadOnlyDictionary<string, JsonValue> preservedUnknownKeys)
        {
            AgentPolicy = agentPolicy;
            HumanPolicy = humanPolicy;
            DestructiveRequiresAsk = destructiveRequiresAsk;
            DiscoveryMode = discoveryMode;
            IntegrityLevel = integrityLevel;
            WhitelistedOperations = ReadOnlyCollections.CopyList(whitelistedOperations);
            WhitelistedTypeProviders = ReadOnlyCollections.CopyList(whitelistedTypeProviders);
            AllowedPaths = ReadOnlyCollections.CopyList(allowedPaths);
            CodePaths = ReadOnlyCollections.CopyList(codePaths);
            CustomOperationFolders = ReadOnlyCollections.CopyList(customOperationFolders);
            AllowedPackageSources = ReadOnlyCollections.CopyList(allowedPackageSources);
            DisabledOperations = ReadOnlyCollections.CopyList(disabledOperations);
            AgentLanguage = agentLanguage;
            DebugLogging = debugLogging;
            TickBudgetMs = tickBudgetMs;
            RetainRuns = retainRuns;
            RetainMegabytes = retainMegabytes;
            ApprovalTimeoutMinutes = approvalTimeoutMinutes;
            MigratedFromV1 = migratedFromV1;
            PreservedUnknownKeys = preservedUnknownKeys;
        }

        /// <summary>
        /// Effective Agent level for every §C4 effect name; missing file keys fall back
        /// to the §H4 example defaults. <c>code.write</c> is never
        /// <see cref="PolicyLevel.Allow"/> here.
        /// </summary>
        public IReadOnlyDictionary<string, PolicyLevel> AgentPolicy { get; }

        /// <summary>
        /// Effective human level for every §C4 effect name. Keys absent from the file
        /// default to <see cref="PolicyLevel.Allow"/> except the three the §H4 example
        /// sets to <see cref="PolicyLevel.Ask"/>.
        /// </summary>
        public IReadOnlyDictionary<string, PolicyLevel> HumanPolicy { get; }

        /// <summary><c>destructiveRequiresAsk</c>, default <c>true</c> (SPEC §H4).</summary>
        public bool DestructiveRequiresAsk { get; }

        /// <summary><c>extensions.discoveryMode</c>, default <see cref="DiscoveryMode.Auto"/>.</summary>
        public DiscoveryMode DiscoveryMode { get; }

        /// <summary><c>extensions.integrityLevel</c>, default <see cref="IntegrityLevel.Weak"/>.</summary>
        public IntegrityLevel IntegrityLevel { get; }

        /// <summary><c>extensions.whitelist.operations</c>, default empty.</summary>
        public IReadOnlyList<OperationWhitelistEntry> WhitelistedOperations { get; }

        /// <summary><c>extensions.whitelist.typeProviders</c>, default empty.</summary>
        public IReadOnlyList<TypeProviderWhitelistEntry> WhitelistedTypeProviders { get; }

        /// <summary><c>allowedPaths</c>, default <c>["Assets/"]</c>.</summary>
        public IReadOnlyList<string> AllowedPaths { get; }

        /// <summary><c>codePaths</c>, default <c>["Assets/Scripts/"]</c>.</summary>
        public IReadOnlyList<string> CodePaths { get; }

        /// <summary><c>customOperationFolders</c>, default <c>["Assets/OpWeave/Editor/Operations/"]</c>.</summary>
        public IReadOnlyList<string> CustomOperationFolders { get; }

        /// <summary>
        /// <c>allowedPackageSources</c>, default <c>["registry:com.unity.*"]</c>. Every
        /// entry satisfied the closed SG-19 grammar at load (SPEC §0.6); matching a
        /// candidate source against them is <see cref="PackageSourcePolicy"/>.
        /// </summary>
        public IReadOnlyList<string> AllowedPackageSources { get; }

        /// <summary><c>disabledOperations</c> (Operation IDs excluded before all else), default empty.</summary>
        public IReadOnlyList<string> DisabledOperations { get; }

        /// <summary><c>agentLanguage</c>, default <c>"en"</c>.</summary>
        public string AgentLanguage { get; }

        /// <summary><c>debugLogging</c>, default <c>false</c>; only JSON booleans are accepted.</summary>
        public bool DebugLogging { get; }

        /// <summary><c>tickBudgetMs</c>, default <c>50</c>.</summary>
        public long TickBudgetMs { get; }

        /// <summary><c>retainRuns</c>, default <c>50</c>.</summary>
        public long RetainRuns { get; }

        /// <summary><c>retainMegabytes</c>, default <c>200</c>.</summary>
        public long RetainMegabytes { get; }

        /// <summary><c>approvalTimeoutMinutes</c>, default <c>1440</c> (SPEC §H4: 24h expiry).</summary>
        public long ApprovalTimeoutMinutes { get; }

        /// <summary>True when a v1 file was migrated to v2 in memory (OW-CFG-004); the file on disk is untouched.</summary>
        public bool MigratedFromV1 { get; }

        /// <summary>
        /// Unknown keys ignored with OW-CFG-003, keyed by JsonPath so a later human save
        /// (SettingsProvider, T-W3.4-04) can write them back unchanged (SPEC §H4.1).
        /// </summary>
        public IReadOnlyDictionary<string, JsonValue> PreservedUnknownKeys { get; }

        // §H4 example defaults for the Agent: ask on move/delete/code.write/package.
        internal static Dictionary<string, PolicyLevel> DefaultAgentPolicy()
        {
            var map = DefaultHumanPolicy();
            map["asset.move"] = PolicyLevel.Ask;
            return map;
        }

        // §H4 example defaults for humans: allow except delete/code.write/package.
        internal static Dictionary<string, PolicyLevel> DefaultHumanPolicy()
        {
            var map = new Dictionary<string, PolicyLevel>(StringComparer.Ordinal);
            foreach (var effect in KnownEffects)
            {
                map[effect] = PolicyLevel.Allow;
            }
            map["asset.delete"] = PolicyLevel.Ask;
            map["code.write"] = PolicyLevel.Ask;
            map["package.modify"] = PolicyLevel.Ask;
            return map;
        }
    }
}
