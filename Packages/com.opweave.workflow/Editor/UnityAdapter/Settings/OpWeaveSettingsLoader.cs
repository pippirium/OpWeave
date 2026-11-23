#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Reads <c>ProjectSettings/OpWeaveSettings.json</c> into <see cref="OpWeaveSettings"/>
    /// under the SPEC §H4.1 loading rules (decision D-R6: fail closed, never silently
    /// downgrade an invalid value to a default).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The returned <see cref="Result{T}"/> is the settings-error channel consumed by the
    /// Registry builder and the Runner host: a failed result carries the OW-CFG-001 /
    /// OW-CFG-002 diagnostics and means "register standard Operations only, accept no new
    /// Runs" (SPEC §H4.1); a successful result may still carry OW-CFG-003/OW-CFG-004/
    /// OW-CFG-005 warnings. There is no write path — v1 files are migrated in memory only and the
    /// file on disk is never touched.
    /// </para>
    /// <para>
    /// Documented local readings where SPEC §H4.1 leaves detail open (all fail closed and
    /// reversible): a JSON document whose root is not an object, a <c>version</c> that is
    /// null, non-integer or less than 1, and I/O failures all count as "cannot read the
    /// settings file" → OW-CFG-001; in a v1 document every other key keeps its v2 meaning
    /// (including <c>extensions</c>) while <c>customOperationTrust</c> additionally
    /// overrides <c>discoveryMode</c> per the migration rules; in a v2 document
    /// <c>customOperationTrust</c> is an unknown key (OW-CFG-003, preserved); a human
    /// <c>policy.human["code.write"]</c> of <c>"allow"</c> is an invalid value —
    /// SPEC §H4 limits the code.write settings range to ask/deny and the §H4.1
    /// allow→ask correction binds <c>policy.agent</c> only (OW-CFG-002);
    /// whitelist entry <c>version</c> must be an integer ≥ 1; numeric settings
    /// accept integers only.
    /// </para>
    /// </remarks>
    public static class OpWeaveSettingsLoader
    {
        /// <summary>VCS-tracked settings file location relative to the project root (SPEC §H4).</summary>
        public const string DefaultFilePath = "ProjectSettings/OpWeaveSettings.json";

        private const string InvalidValueMessage = "Settings key has an invalid value.";
        private const string MissingWhitelistKeyMessage =
            "Settings whitelist entry is missing a required key.";

        /// <summary>
        /// Loads <see cref="DefaultFilePath"/>; a missing file yields
        /// <see cref="OpWeaveSettings.Default"/> with no diagnostics.
        /// </summary>
        public static Result<OpWeaveSettings> Load() => Load(DefaultFilePath);

        /// <summary>
        /// Loads the settings file at <paramref name="path"/>. Only a confirmed
        /// missing file (or missing parent directory) yields
        /// <see cref="OpWeaveSettings.Default"/> with no diagnostics; every other
        /// read failure — access denied, a directory at the path, an I/O error —
        /// is "cannot read the settings file" (OW-CFG-001), never permissive
        /// defaults.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        public static Result<OpWeaveSettings> Load(string path)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                return Result<OpWeaveSettings>.Success(OpWeaveSettings.Default);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is NotSupportedException)
            {
                return Result<OpWeaveSettings>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_CFG_001,
                    "Settings file could not be read.",
                    jsonPath: "$",
                    hint: ex.GetType().Name));
            }
            return Parse(text);
        }

        /// <summary>
        /// Parses <paramref name="json"/> as settings file content (no version key means
        /// v1, per SPEC §H4.1). Diagnostics follow the §H4.1 loading table.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        public static Result<OpWeaveSettings> Parse(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var parsed = JsonValue.Parse(json);
            if (!parsed.Ok)
            {
                // SG-02: map the reader's internal (empty-code, payload-free) diagnostic
                // to OW-CFG-001 exactly once.
                var mapped = new List<Diagnostic>(parsed.Diagnostics.Count);
                foreach (var diagnostic in parsed.Diagnostics)
                {
                    mapped.Add(ErrorCodes.Create(
                        ErrorCodes.OW_CFG_001,
                        diagnostic.Message,
                        jsonPath: diagnostic.JsonPath));
                }
                return Result<OpWeaveSettings>.Failure(mapped);
            }
            if (parsed.Value!.ToToken() is not JObject root)
            {
                return Result<OpWeaveSettings>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_CFG_001,
                    "Settings file root must be a JSON object.",
                    jsonPath: "$"));
            }
            return new Reader(root).Run();
        }

        private sealed class Reader
        {
            private readonly JObject _root;
            private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
            private readonly Dictionary<string, JsonValue> _preserved =
                new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            private bool _hasError;

            internal Reader(JObject root)
            {
                _root = root;
            }

            internal Result<OpWeaveSettings> Run()
            {
                // version decides the reading: absent/1 → v1 migrate in memory, 2 → as-is,
                // anything else → OW-CFG-001 (SPEC §H4.1 loading table).
                var versionToken = _root["version"];
                bool isV1;
                if (versionToken is null || IsInteger(versionToken, 1))
                {
                    isV1 = true;
                }
                else if (IsInteger(versionToken, 2))
                {
                    isV1 = false;
                }
                else
                {
                    _diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_CFG_001,
                        "Settings file has an unknown version.",
                        jsonPath: "$.version"));
                    return Result<OpWeaveSettings>.Failure(_diagnostics);
                }

                var agentPolicy = OpWeaveSettings.DefaultAgentPolicy();
                var humanPolicy = OpWeaveSettings.DefaultHumanPolicy();
                var destructiveRequiresAsk = true;
                var discoveryMode = DiscoveryMode.Auto;
                var integrityLevel = IntegrityLevel.Weak;
                var operations = new List<OperationWhitelistEntry>();
                var typeProviders = new List<TypeProviderWhitelistEntry>();
                var allowedPaths = new List<string> { "Assets/" };
                var codePaths = new List<string> { "Assets/Scripts/" };
                var customOperationFolders = new List<string> { "Assets/OpWeave/Editor/Operations/" };
                var allowedPackageSources = new List<string> { "registry:com.unity.*" };
                var disabledOperations = new List<string>();
                var agentLanguage = "en";
                var debugLogging = false;
                long tickBudgetMs = 50, retainRuns = 50, retainMegabytes = 200, approvalTimeoutMinutes = 1440;
                string? customOperationTrust = null;
                var whitelistForcedEmpty = false;

                foreach (var member in _root.Properties())
                {
                    var path = MemberPath("$", member.Name);
                    switch (member.Name)
                    {
                        case "version":
                            break;
                        case "customOperationTrust":
                            // v1-migration-only key (SPEC §H4.1); unknown in a v2 file.
                            if (!isV1)
                            {
                                Preserve(path, member.Value);
                            }
                            else if (member.Value.Type != JTokenType.String)
                            {
                                InvalidValue(path);
                            }
                            else
                            {
                                customOperationTrust = member.Value.Value<string>();
                            }
                            break;
                        case "policy":
                            ReadPolicy(member.Value, path, agentPolicy, humanPolicy);
                            break;
                        case "destructiveRequiresAsk":
                            destructiveRequiresAsk = ReadBoolean(member.Value, path, destructiveRequiresAsk);
                            break;
                        case "extensions":
                            ReadExtensions(
                                member.Value, path,
                                mode => discoveryMode = mode,
                                level => integrityLevel = level,
                                operations, typeProviders);
                            break;
                        case "allowedPaths":
                            ReadStringArray(member.Value, path, allowedPaths);
                            break;
                        case "codePaths":
                            ReadStringArray(member.Value, path, codePaths);
                            break;
                        case "customOperationFolders":
                            ReadStringArray(member.Value, path, customOperationFolders);
                            break;
                        case "allowedPackageSources":
                            ReadPackageSourceArray(member.Value, path, allowedPackageSources);
                            break;
                        case "disabledOperations":
                            ReadStringArray(member.Value, path, disabledOperations);
                            break;
                        case "agentLanguage":
                            agentLanguage = ReadString(member.Value, path, agentLanguage);
                            break;
                        case "debugLogging":
                            debugLogging = ReadBoolean(member.Value, path, debugLogging);
                            break;
                        case "tickBudgetMs":
                            tickBudgetMs = ReadInteger(member.Value, path, tickBudgetMs);
                            break;
                        case "retainRuns":
                            retainRuns = ReadInteger(member.Value, path, retainRuns);
                            break;
                        case "retainMegabytes":
                            retainMegabytes = ReadInteger(member.Value, path, retainMegabytes);
                            break;
                        case "approvalTimeoutMinutes":
                            approvalTimeoutMinutes = ReadInteger(member.Value, path, approvalTimeoutMinutes);
                            break;
                        default:
                            Preserve(path, member.Value);
                            break;
                    }
                }

                if (isV1)
                {
                    // SPEC §H4.1 migration (memory only, file untouched):
                    // auto → discoveryMode auto; review → whitelist mode + empty whitelist.
                    string? hint;
                    switch (customOperationTrust)
                    {
                        case "auto":
                            discoveryMode = DiscoveryMode.Auto;
                            hint = "customOperationTrust \"auto\" migrated to extensions.discoveryMode \"auto\".";
                            break;
                        case "review":
                            discoveryMode = DiscoveryMode.Whitelist;
                            whitelistForcedEmpty = true;
                            hint = "customOperationTrust \"review\" migrated to extensions.discoveryMode \"whitelist\" with an empty whitelist.";
                            break;
                        case null:
                            hint = "No customOperationTrust key; remaining keys read with their v2 meaning.";
                            break;
                        default:
                            InvalidValue("$.customOperationTrust");
                            hint = null;
                            break;
                    }
                    _diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_CFG_004,
                        "Settings file was read as v1 and migrated to v2 in memory; the file was not modified.",
                        hint: hint));
                }

                if (whitelistForcedEmpty)
                {
                    operations.Clear();
                    typeProviders.Clear();
                }

                if (_hasError)
                {
                    return Result<OpWeaveSettings>.Failure(_diagnostics);
                }

                var settings = new OpWeaveSettings(
                    agentPolicy: ReadOnlyCollections.CopyMap(agentPolicy),
                    humanPolicy: ReadOnlyCollections.CopyMap(humanPolicy),
                    destructiveRequiresAsk: destructiveRequiresAsk,
                    discoveryMode: discoveryMode,
                    integrityLevel: integrityLevel,
                    whitelistedOperations: operations,
                    whitelistedTypeProviders: typeProviders,
                    allowedPaths: allowedPaths,
                    codePaths: codePaths,
                    customOperationFolders: customOperationFolders,
                    allowedPackageSources: allowedPackageSources,
                    disabledOperations: disabledOperations,
                    agentLanguage: agentLanguage,
                    debugLogging: debugLogging,
                    tickBudgetMs: tickBudgetMs,
                    retainRuns: retainRuns,
                    retainMegabytes: retainMegabytes,
                    approvalTimeoutMinutes: approvalTimeoutMinutes,
                    migratedFromV1: isV1,
                    preservedUnknownKeys: ReadOnlyCollections.CopyMap(_preserved));
                return _diagnostics.Count == 0
                    ? Result<OpWeaveSettings>.Success(settings)
                    : Result<OpWeaveSettings>.Success(settings, _diagnostics);
            }

            private void ReadPolicy(
                JToken token, string path,
                IDictionary<string, PolicyLevel> agentPolicy,
                IDictionary<string, PolicyLevel> humanPolicy)
            {
                if (token is not JObject policy)
                {
                    InvalidValue(path);
                    return;
                }
                foreach (var member in policy.Properties())
                {
                    var memberPath = MemberPath(path, member.Name);
                    switch (member.Name)
                    {
                        case "agent":
                            ReadPolicyMap(member.Value, memberPath, agentPolicy, agent: true);
                            break;
                        case "human":
                            ReadPolicyMap(member.Value, memberPath, humanPolicy, agent: false);
                            break;
                        default:
                            Preserve(memberPath, member.Value);
                            break;
                    }
                }
            }

            private void ReadPolicyMap(
                JToken token, string path,
                IDictionary<string, PolicyLevel> levels,
                bool agent)
            {
                if (token is not JObject map)
                {
                    InvalidValue(path);
                    return;
                }
                var known = new HashSet<string>(OpWeaveSettings.KnownEffects, StringComparer.Ordinal);
                foreach (var member in map.Properties())
                {
                    var memberPath = MemberPath(path, member.Name);
                    if (!known.Contains(member.Name))
                    {
                        Preserve(memberPath, member.Value);
                        continue;
                    }
                    if (member.Value.Type != JTokenType.String)
                    {
                        InvalidValue(memberPath);
                        continue;
                    }
                    switch (member.Value.Value<string>())
                    {
                        case "allow":
                            if (member.Name == "code.write")
                            {
                                if (agent)
                                {
                                    // SPEC §H4 stage 3 / SG-18: an Agent code.write of
                                    // "allow" is treated as ask with exactly one
                                    // OW-CFG-005 warning (the reader rejects duplicate
                                    // keys, so this member occurs at most once).
                                    levels[member.Name] = PolicyLevel.Ask;
                                    _diagnostics.Add(ErrorCodes.Create(
                                        ErrorCodes.OW_CFG_005,
                                        "Agent code.write policy \"allow\" is not permitted; it is applied as \"ask\".",
                                        jsonPath: memberPath));
                                }
                                else
                                {
                                    // SPEC §C4/§H4: the code.write settings range is
                                    // ask/deny only and the §H4.1 correction applies to
                                    // policy.agent alone — a human "allow" is an
                                    // invalid value of a known key (OW-CFG-002).
                                    InvalidValue(memberPath);
                                }
                            }
                            else
                            {
                                levels[member.Name] = PolicyLevel.Allow;
                            }
                            break;
                        case "ask":
                            levels[member.Name] = PolicyLevel.Ask;
                            break;
                        case "deny":
                            levels[member.Name] = PolicyLevel.Deny;
                            break;
                        default:
                            InvalidValue(memberPath);
                            break;
                    }
                }
            }

            private void ReadExtensions(
                JToken token, string path,
                Action<DiscoveryMode> setDiscoveryMode,
                Action<IntegrityLevel> setIntegrityLevel,
                List<OperationWhitelistEntry> operations,
                List<TypeProviderWhitelistEntry> typeProviders)
            {
                if (token is not JObject extensions)
                {
                    InvalidValue(path);
                    return;
                }
                foreach (var member in extensions.Properties())
                {
                    var memberPath = MemberPath(path, member.Name);
                    switch (member.Name)
                    {
                        case "discoveryMode":
                            switch (member.Value.Type == JTokenType.String
                                    ? member.Value.Value<string>()
                                    : null)
                            {
                                case "auto":
                                    setDiscoveryMode(DiscoveryMode.Auto);
                                    break;
                                case "whitelist":
                                    setDiscoveryMode(DiscoveryMode.Whitelist);
                                    break;
                                default:
                                    InvalidValue(memberPath);
                                    break;
                            }
                            break;
                        case "integrityLevel":
                            switch (member.Value.Type == JTokenType.String
                                    ? member.Value.Value<string>()
                                    : null)
                            {
                                case "weak":
                                    setIntegrityLevel(IntegrityLevel.Weak);
                                    break;
                                case "full":
                                    // Valid value; full is rejected at Run acceptance
                                    // with OW-OP-011 (SPEC §H4.1), never downgraded.
                                    setIntegrityLevel(IntegrityLevel.Full);
                                    break;
                                default:
                                    InvalidValue(memberPath);
                                    break;
                            }
                            break;
                        case "whitelist":
                            ReadWhitelist(member.Value, memberPath, operations, typeProviders);
                            break;
                        default:
                            Preserve(memberPath, member.Value);
                            break;
                    }
                }
            }

            private void ReadWhitelist(
                JToken token, string path,
                List<OperationWhitelistEntry> operations,
                List<TypeProviderWhitelistEntry> typeProviders)
            {
                if (token is not JObject whitelist)
                {
                    InvalidValue(path);
                    return;
                }
                foreach (var member in whitelist.Properties())
                {
                    var memberPath = MemberPath(path, member.Name);
                    switch (member.Name)
                    {
                        case "operations":
                            ReadEntryArray(
                                member.Value, memberPath,
                                (entry, index) => ReadOperationEntry(entry, index, operations));
                            break;
                        case "typeProviders":
                            ReadEntryArray(
                                member.Value, memberPath,
                                (entry, index) => ReadTypeProviderEntry(entry, index, typeProviders));
                            break;
                        default:
                            Preserve(memberPath, member.Value);
                            break;
                    }
                }
            }

            private void ReadEntryArray(JToken token, string path, Action<JObject, string> readEntry)
            {
                if (token is not JArray array)
                {
                    InvalidValue(path);
                    return;
                }
                for (var i = 0; i < array.Count; i++)
                {
                    var elementPath = path + "[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                    if (array[i] is JObject entry)
                    {
                        readEntry(entry, elementPath);
                    }
                    else
                    {
                        InvalidValue(elementPath);
                    }
                }
            }

            private void ReadOperationEntry(
                JObject entry, string path, List<OperationWhitelistEntry> operations)
            {
                var packageId = WhitelistString(entry, "packageId", path);
                var assemblyName = WhitelistString(entry, "assemblyName", path);
                var operationId = WhitelistString(entry, "operationId", path);
                long? version = null;
                var versionToken = entry["version"];
                var versionPath = MemberPath(path, "version");
                if (versionToken is null)
                {
                    MissingWhitelistKey(versionPath);
                }
                else if (versionToken.Type != JTokenType.Integer || versionToken.Value<long>() < 1)
                {
                    InvalidValue(versionPath);
                }
                else
                {
                    version = versionToken.Value<long>();
                }
                foreach (var member in entry.Properties())
                {
                    if (member.Name is not ("packageId" or "assemblyName" or "operationId" or "version"))
                    {
                        Preserve(MemberPath(path, member.Name), member.Value);
                    }
                }
                if (packageId is not null && assemblyName is not null &&
                    operationId is not null && version is not null)
                {
                    operations.Add(new OperationWhitelistEntry(
                        packageId, assemblyName, operationId, version.Value));
                }
            }

            private void ReadTypeProviderEntry(
                JObject entry, string path, List<TypeProviderWhitelistEntry> typeProviders)
            {
                var packageId = WhitelistString(entry, "packageId", path);
                var providerId = WhitelistString(entry, "providerId", path);
                foreach (var member in entry.Properties())
                {
                    if (member.Name is not ("packageId" or "providerId"))
                    {
                        Preserve(MemberPath(path, member.Name), member.Value);
                    }
                }
                if (packageId is not null && providerId is not null)
                {
                    typeProviders.Add(new TypeProviderWhitelistEntry(packageId, providerId));
                }
            }

            // Wildcards, empty strings and missing keys are invalid values (SPEC §H4.1).
            private string? WhitelistString(JObject entry, string key, string path)
            {
                var memberPath = MemberPath(path, key);
                var token = entry[key];
                if (token is null)
                {
                    MissingWhitelistKey(memberPath);
                    return null;
                }
                if (token.Type != JTokenType.String)
                {
                    InvalidValue(memberPath);
                    return null;
                }
                var value = token.Value<string>()!;
                if (value.Length == 0 || value.IndexOf('*') >= 0)
                {
                    InvalidValue(memberPath);
                    return null;
                }
                return value;
            }

            private bool ReadBoolean(JToken token, string path, bool fallback)
            {
                if (token.Type == JTokenType.Boolean)
                {
                    return token.Value<bool>();
                }
                InvalidValue(path);
                return fallback;
            }

            private long ReadInteger(JToken token, string path, long fallback)
            {
                if (token.Type == JTokenType.Integer)
                {
                    return token.Value<long>();
                }
                InvalidValue(path);
                return fallback;
            }

            private string ReadString(JToken token, string path, string fallback)
            {
                if (token.Type == JTokenType.String)
                {
                    return token.Value<string>()!;
                }
                InvalidValue(path);
                return fallback;
            }

            // Every element must also satisfy the closed SG-19 grammar
            // (SPEC §0.6): an out-of-grammar entry is an invalid value at its own
            // element JsonPath, never a silently dropped allowlist entry.
            private void ReadPackageSourceArray(JToken token, string path, List<string> target)
            {
                if (token is not JArray array)
                {
                    InvalidValue(path);
                    return;
                }
                var values = new List<string>(array.Count);
                var valid = true;
                for (var i = 0; i < array.Count; i++)
                {
                    var elementPath = path + "[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                    if (array[i].Type != JTokenType.String)
                    {
                        InvalidValue(elementPath);
                        valid = false;
                        continue;
                    }
                    var entry = array[i].Value<string>()!;
                    if (!PackageSourceRule.TryParse(entry, out _))
                    {
                        InvalidPackageSource(elementPath);
                        valid = false;
                        continue;
                    }
                    values.Add(entry);
                }
                if (valid)
                {
                    target.Clear();
                    target.AddRange(values);
                }
            }

            private void ReadStringArray(JToken token, string path, List<string> target)
            {
                if (token is not JArray array)
                {
                    InvalidValue(path);
                    return;
                }
                var values = new List<string>(array.Count);
                var valid = true;
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i].Type == JTokenType.String)
                    {
                        values.Add(array[i].Value<string>()!);
                    }
                    else
                    {
                        InvalidValue(path + "[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]");
                        valid = false;
                    }
                }
                if (valid)
                {
                    target.Clear();
                    target.AddRange(values);
                }
            }

            private void InvalidValue(string jsonPath)
            {
                _hasError = true;
                _diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_CFG_002, InvalidValueMessage, jsonPath: jsonPath));
            }

            private void InvalidPackageSource(string jsonPath)
            {
                _hasError = true;
                _diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_CFG_002,
                    InvalidValueMessage,
                    jsonPath: jsonPath,
                    hint: "allowedPackageSources entries must match the closed source grammar (registry, git https or git ssh)."));
            }

            private void MissingWhitelistKey(string jsonPath)
            {
                _hasError = true;
                _diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_CFG_002, MissingWhitelistKeyMessage, jsonPath: jsonPath));
            }

            private void Preserve(string jsonPath, JToken value)
            {
                _preserved[jsonPath] = JsonValue.From(value);
                _diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_CFG_003, "Unknown settings key ignored.", jsonPath: jsonPath));
            }

            private static bool IsInteger(JToken token, long expected) =>
                token.Type == JTokenType.Integer && token.Value<long>() == expected;
        }

        // JsonPath member steps use dotted form for plain identifiers and the bracketed
        // form for names like "code.write" (SPEC §0.6 SG-18: $.policy.agent["code.write"]).
        private static string MemberPath(string parent, string name)
        {
            if (IsSimpleName(name))
            {
                return parent + "." + name;
            }
            return parent + "[\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]";
        }

        private static bool IsSimpleName(string name)
        {
            if (name.Length == 0)
            {
                return false;
            }
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var ok = c == '_' ||
                    (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (i > 0 && c >= '0' && c <= '9');
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
