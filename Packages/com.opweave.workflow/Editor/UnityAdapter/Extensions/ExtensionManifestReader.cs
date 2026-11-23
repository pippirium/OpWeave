#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Reads <c>opweave.extension.json</c> into <see cref="ExtensionManifest"/>
    /// and applies every manifest-local rule of SPEC §X.3.2 and §B0.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checks here need no environment data: JSON syntax and shape,
    /// <c>schemaVersion</c> "1.0", a nonempty grammatically valid
    /// <c>extensionId</c> without a reserved prefix, the <c>coreApi</c>
    /// range parsed by <see cref="CoreApiRange.Parse"/> and required to
    /// contain <see cref="CoreApi.Current"/> (OW-OP-012, SPEC §B0.2 rules
    /// 1–3), the <c>unity.minInclusive</c> format, the element shapes of
    /// <c>assemblies</c> / <c>operations</c> / <c>typeProviders</c> /
    /// <c>examples</c>, in-manifest duplicate and reserved-namespace
    /// rejection, and the v1 Blueprint reservation — a non-empty
    /// <c>blueprints</c> list is refused with OW-OP-010 (SPEC §X.10.4, §J.1).
    /// </para>
    /// <para>
    /// Documented local readings where the SPEC is silent (all fail closed
    /// and reversible): <c>schemaVersion</c>, <c>extensionId</c>,
    /// <c>coreApi</c> and <c>unity.minInclusive</c> are required keys;
    /// <c>displayName</c>, <c>assemblies</c>, <c>operations</c>,
    /// <c>typeProviders</c>, <c>examples</c> and <c>blueprints</c> are
    /// optional and default to absent/empty; <c>extensionId</c> and Provider
    /// IDs follow the Operation ID grammar of SPEC §A4 (the same dotted
    /// lowercase shape every SPEC example uses); unknown keys are ignored
    /// (<c>sourceRepository</c>/<c>resolvedRevision</c> are UPM-resolved
    /// audit data, never self-declared manifest input — T-W2.12-02).
    /// </para>
    /// <para>
    /// The published codes are OW-OP-010 (non-empty Blueprints),
    /// OW-OP-012 (coreApi) and OW-OP-007 (a declared custom Operation ID
    /// in a reserved namespace — the code's §J meaning applies exactly).
    /// Every other manifest-local rejection stays an internal diagnostic
    /// (empty code, fixed English — the SG-02 convention): a malformed or
    /// self-inconsistent manifest is not the OW-OP-018
    /// declaration-vs-entity mismatch (SPEC §J.1, T-W2.12-03).
    /// </para>
    /// </remarks>
    public static class ExtensionManifestReader
    {
        /// <summary>The manifest file name at the UPM package root (SPEC §X.2).</summary>
        public const string FileName = "opweave.extension.json";

        /// <summary>The only supported <c>schemaVersion</c> (SPEC §X.3.2).</summary>
        public const string SupportedSchemaVersion = "1.0";

        /// <summary>
        /// Parses and manifest-locally validates <paramref name="json"/>.
        /// Every discovered problem is reported; a failure result carries all
        /// of them.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        public static Result<ExtensionManifest> Parse(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var parsed = JsonValue.Parse(json);
            if (!parsed.Ok)
            {
                // SG-02 internal diagnostics pass through unchanged: an
                // unparseable manifest declares nothing, so OW-OP-018's
                // declared-vs-observed meaning does not apply (SPEC §J.1).
                return Result<ExtensionManifest>.Failure(parsed.Diagnostics);
            }
            if (parsed.Value!.ToToken() is not JObject root)
            {
                return Result<ExtensionManifest>.Failure(Internal(
                    "Extension manifest root must be a JSON object.", "$"));
            }
            return new Reader(root).Run();
        }

        private sealed class Reader
        {
            private readonly JObject _root;
            private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();

            internal Reader(JObject root)
            {
                _root = root;
            }

            internal Result<ExtensionManifest> Run()
            {
                var schemaVersion = ReadSchemaVersion();
                var extensionId = ReadExtensionId();
                var displayName = ReadDisplayName();
                var coreApi = ReadCoreApi();
                var unityMin = ReadUnityMinInclusive();
                var assemblies = ReadStringArray("assemblies", allowDuplicates: false);
                var operations = ReadOperations();
                var typeProviders = ReadTypeProviders(assemblies);
                var examples = ReadStringArray("examples", allowDuplicates: true);
                ReadBlueprints();

                if (_diagnostics.Count > 0)
                {
                    return Result<ExtensionManifest>.Failure(_diagnostics);
                }
                return Result<ExtensionManifest>.Success(new ExtensionManifest(
                    schemaVersion!,
                    extensionId!,
                    displayName,
                    coreApi!,
                    unityMin!,
                    assemblies,
                    operations,
                    typeProviders,
                    examples,
                    Array.Empty<string>()));
            }

            private string? ReadSchemaVersion()
            {
                var token = _root["schemaVersion"];
                if (token is null || token.Type != JTokenType.String)
                {
                    _diagnostics.Add(Internal(
                        "Extension manifest is missing required key 'schemaVersion'.",
                        "$.schemaVersion"));
                    return null;
                }
                var value = token.Value<string>()!;
                if (!string.Equals(value, SupportedSchemaVersion, StringComparison.Ordinal))
                {
                    _diagnostics.Add(Internal(
                        $"Extension manifest schemaVersion '{value}' is not supported.",
                        "$.schemaVersion",
                        $"Supported schemaVersion: '{SupportedSchemaVersion}' (SPEC §X.3.2)."));
                    return null;
                }
                return value;
            }

            private string? ReadExtensionId()
            {
                var token = _root["extensionId"];
                if (token is null || token.Type != JTokenType.String || token.Value<string>()!.Length == 0)
                {
                    _diagnostics.Add(Internal(
                        "Extension manifest is missing required key 'extensionId'.",
                        "$.extensionId"));
                    return null;
                }
                var value = token.Value<string>()!;
                if (!IdGrammar.IsValidOperationId(value))
                {
                    _diagnostics.Add(Internal(
                        $"Extension manifest extensionId '{value}' is malformed.",
                        "$.extensionId"));
                    return null;
                }
                if (IdGrammar.HasReservedOperationPrefix(value))
                {
                    _diagnostics.Add(Internal(
                        $"Extension manifest extensionId '{value}' uses a reserved namespace.",
                        "$.extensionId",
                        "core.*, unity.* and opweave.* are reserved (SPEC §X.3.2)."));
                    return null;
                }
                return value;
            }

            private string? ReadDisplayName()
            {
                var token = _root["displayName"];
                if (token is null)
                {
                    return null;
                }
                if (token.Type != JTokenType.String || token.Value<string>()!.Length == 0)
                {
                    _diagnostics.Add(Internal(
                        "Extension manifest 'displayName' must be a non-empty string.",
                        "$.displayName"));
                    return null;
                }
                return token.Value<string>();
            }

            // SPEC §B0.2 rules 1–3: both bounds parsed by CoreApiRange.Parse,
            // min < max, and the range must contain CoreApi.Current — every
            // failure is OW-OP-012 with the declared range and the current
            // Core version in the hint.
            private CoreApiRange? ReadCoreApi()
            {
                var token = _root["coreApi"];
                if (token is not JObject coreApi)
                {
                    _diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_012,
                        "Extension manifest 'coreApi' is missing or not an object.",
                        jsonPath: "$.coreApi",
                        hint: $"The extension declares no range. Current CoreApi.Version: {CoreApi.Version}."));
                    return null;
                }
                var min = BoundString(coreApi, "minInclusive");
                var max = BoundString(coreApi, "maxExclusive");
                if (min is null || max is null)
                {
                    _diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_012,
                        "Extension manifest 'coreApi' is missing a bound or a bound is not a string.",
                        jsonPath: "$.coreApi",
                        hint: $"Declared range: '{min ?? "<missing>"}'..'{max ?? "<missing>"}'. Current CoreApi.Version: {CoreApi.Version}."));
                    return null;
                }
                var range = CoreApiRange.Parse(min, max);
                if (!range.Ok)
                {
                    _diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_012,
                        "Extension manifest 'coreApi' range is invalid.",
                        jsonPath: "$.coreApi",
                        hint: $"Declared range: '{min}'..'{max}'. Current CoreApi.Version: {CoreApi.Version}."));
                    return null;
                }
                if (!range.Value!.Contains(CoreApi.Current))
                {
                    _diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_012,
                        "Extension manifest 'coreApi' range does not contain the current Core public API version.",
                        jsonPath: "$.coreApi",
                        hint: $"Declared range: '{min}'..'{max}'. Current CoreApi.Version: {CoreApi.Version}."));
                    return null;
                }
                return range.Value;
            }

            private static string? BoundString(JObject coreApi, string key)
            {
                var token = coreApi[key];
                return token is not null && token.Type == JTokenType.String
                    ? token.Value<string>()
                    : null;
            }

            private string? ReadUnityMinInclusive()
            {
                var token = _root["unity"];
                if (token is not JObject unity)
                {
                    _diagnostics.Add(Internal(
                        "Extension manifest 'unity' is missing or not an object.",
                        "$.unity"));
                    return null;
                }
                var min = unity["minInclusive"];
                if (min is null || min.Type != JTokenType.String)
                {
                    _diagnostics.Add(Internal(
                        "Extension manifest 'unity.minInclusive' is missing or not a string.",
                        "$.unity.minInclusive"));
                    return null;
                }
                var value = min.Value<string>()!;
                if (!ExtensionManifestValidator.IsUnityVersionText(value))
                {
                    _diagnostics.Add(Internal(
                        $"Extension manifest 'unity.minInclusive' '{value}' is malformed.",
                        "$.unity.minInclusive",
                        "Expected MAJOR.MINOR or MAJOR.MINOR.PATCH (SPEC §X.3.2)."));
                    return null;
                }
                return value;
            }

            private List<string> ReadStringArray(string key, bool allowDuplicates)
            {
                var result = new List<string>();
                var token = _root[key];
                if (token is null)
                {
                    return result;
                }
                if (token is not JArray array)
                {
                    _diagnostics.Add(Internal(
                        $"Extension manifest '{key}' must be an array of strings.",
                        "$." + key));
                    return result;
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    var path = $"$.{key}[{i}]";
                    if (element.Type != JTokenType.String || element.Value<string>()!.Length == 0)
                    {
                        _diagnostics.Add(Internal(
                            $"Extension manifest '{key}' element must be a non-empty string.",
                            path));
                        continue;
                    }
                    var value = element.Value<string>()!;
                    if (!allowDuplicates && !seen.Add(value))
                    {
                        _diagnostics.Add(Internal(
                            $"Extension manifest '{key}' contains duplicate entry '{value}'.",
                            path));
                        continue;
                    }
                    result.Add(value);
                }
                return result;
            }

            private List<ExtensionOperationRef> ReadOperations()
            {
                var result = new List<ExtensionOperationRef>();
                var token = _root["operations"];
                if (token is null)
                {
                    return result;
                }
                if (token is not JArray array)
                {
                    _diagnostics.Add(Internal(
                        "Extension manifest 'operations' must be an array of {id, version} objects.",
                        "$.operations"));
                    return result;
                }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < array.Count; i++)
                {
                    var path = $"$.operations[{i}]";
                    var entry = array[i] as JObject;
                    var id = entry?["id"];
                    var version = entry?["version"];
                    // Operation versions are stored as int
                    // (ExtensionOperationRef.Version): a declared version
                    // outside [1, int.MaxValue] is rejected, never truncated
                    // into a false match with a real Descriptor identity.
                    var versionLong = version?.Type == JTokenType.Integer &&
                                      version is JValue jv && jv.Value is long declared
                        ? declared
                        : (long?)null;
                    if (entry is null || id is null || versionLong is null ||
                        id.Type != JTokenType.String || id.Value<string>()!.Length == 0 ||
                        versionLong < 1 || versionLong > int.MaxValue)
                    {
                        _diagnostics.Add(Internal(
                            $"Extension manifest 'operations' entry must declare a non-empty 'id' string and an integer 'version' between 1 and {int.MaxValue}.",
                            path));
                        continue;
                    }
                    var idText = id.Value<string>()!;
                    if (!IdGrammar.IsValidOperationId(idText))
                    {
                        _diagnostics.Add(Internal(
                            $"Extension manifest Operation ID '{idText}' is malformed.",
                            path + ".id"));
                        continue;
                    }
                    if (IdGrammar.HasReservedOperationPrefix(idText))
                    {
                        // A declared custom Operation in a reserved
                        // namespace — OW-OP-007's §J meaning applies
                        // exactly (SPEC §C3 rule 3).
                        _diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_OP_007,
                            $"Extension manifest Operation ID '{idText}' uses a reserved namespace.",
                            jsonPath: path + ".id",
                            hint: "core.*, unity.* and opweave.* are reserved (SPEC §X.3.2)."));
                        continue;
                    }
                    var versionValue = (int)versionLong.Value;
                    // SPEC §X.3.2 bans duplicate Operation IDs — keyed by
                    // the ID alone, so the same ID at two versions inside
                    // one manifest is still a duplicate.
                    if (!seen.Add(idText))
                    {
                        _diagnostics.Add(Internal(
                            $"Extension manifest declares duplicate Operation ID '{idText}'.",
                            path));
                        continue;
                    }
                    result.Add(new ExtensionOperationRef(idText, versionValue));
                }
                return result;
            }

            private List<ExtensionTypeProviderRef> ReadTypeProviders(IReadOnlyList<string> assemblies)
            {
                var result = new List<ExtensionTypeProviderRef>();
                var token = _root["typeProviders"];
                if (token is null)
                {
                    return result;
                }
                if (token is not JArray array)
                {
                    _diagnostics.Add(Internal(
                        "Extension manifest 'typeProviders' must be an array of {id, assembly} objects.",
                        "$.typeProviders"));
                    return result;
                }
                var declaredAssemblies = new HashSet<string>(assemblies, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < array.Count; i++)
                {
                    var path = $"$.typeProviders[{i}]";
                    var entry = array[i] as JObject;
                    var id = entry?["id"];
                    var assembly = entry?["assembly"];
                    if (entry is null || id is null || assembly is null ||
                        id.Type != JTokenType.String || id.Value<string>()!.Length == 0 ||
                        assembly.Type != JTokenType.String || assembly.Value<string>()!.Length == 0)
                    {
                        _diagnostics.Add(Internal(
                            "Extension manifest 'typeProviders' entry must declare a non-empty 'id' and 'assembly' string.",
                            path));
                        continue;
                    }
                    var idText = id.Value<string>()!;
                    var assemblyText = assembly.Value<string>()!;
                    if (!IdGrammar.IsValidOperationId(idText))
                    {
                        _diagnostics.Add(Internal(
                            $"Extension manifest Provider ID '{idText}' is malformed.",
                            path + ".id"));
                        continue;
                    }
                    if (IdGrammar.HasReservedOperationPrefix(idText))
                    {
                        _diagnostics.Add(Internal(
                            $"Extension manifest Provider ID '{idText}' uses a reserved namespace.",
                            path + ".id",
                            "core.*, unity.* and opweave.* are reserved (SPEC §X.3.2)."));
                        continue;
                    }
                    if (!seen.Add(idText))
                    {
                        _diagnostics.Add(Internal(
                            $"Extension manifest declares duplicate Provider ID '{idText}'.",
                            path + ".id"));
                        continue;
                    }
                    if (!declaredAssemblies.Contains(assemblyText))
                    {
                        _diagnostics.Add(Internal(
                            $"Extension manifest Provider ID '{idText}' names assembly '{assemblyText}', which is not listed in 'assemblies'.",
                            path + ".assembly"));
                        continue;
                    }
                    result.Add(new ExtensionTypeProviderRef(idText, assemblyText));
                }
                return result;
            }

            // SPEC §X.10.4 / §J.1: v1 registers and runs no Blueprints — a
            // non-empty 'blueprints' list is OW-OP-010.
            private void ReadBlueprints()
            {
                var token = _root["blueprints"];
                if (token is null)
                {
                    return;
                }
                if (token is not JArray array)
                {
                    _diagnostics.Add(Internal(
                        "Extension manifest 'blueprints' must be an array.",
                        "$.blueprints"));
                    return;
                }
                if (array.Count > 0)
                {
                    _diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_010,
                        "Extension manifest declares Blueprints, which are not supported in v1.",
                        jsonPath: "$.blueprints",
                        hint: "Blueprint registration and execution are reserved for a future version (SPEC §X.10.4)."));
                }
            }
        }

        // Manifest-local verdicts that are not OW-OP-010/OW-OP-012/
        // OW-OP-007 stay internal diagnostics: SPEC §J.1 assigns no
        // published code to a malformed manifest document (empty code,
        // fixed English — the SG-02 convention, T-W2.12-03).
        private static Diagnostic Internal(string message, string jsonPath, string? hint = null) =>
            new Diagnostic(string.Empty, Severity.Error, message, JsonPath: jsonPath, Hint: hint);
    }
}
