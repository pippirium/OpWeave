#nullable enable
// T-W2.12-01 [Edit] tests (pure validation half): SPEC §X.3.2
// opweave.extension.json validation — schemaVersion, extensionId,
// coreApi range via CoreApiRange.Parse (OW-OP-012 with range + current
// version in the hint, SPEC §B0.2), unity minimum, assemblies, operations
// list equal to actual Descriptor identities, typeProviders present in
// the named assembly, examples paths, non-empty blueprints → OW-OP-010
// (SPEC §X.10.4), duplicate/reserved IDs and whole-extension rejection.
//
// These tests drive ExtensionManifestValidator.Validate / ValidateAll with
// synthesized ExtensionValidationInput; the live discovery pass over real
// embedded fixture packages is ExtensionDiscoveryTests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.12-01:extension-metadata — the validation matrix of
    /// SPEC §X.3.2 / §B0.2 / §X.10.4 against synthesized package inputs.
    /// </summary>
    internal sealed class ExtensionManifestValidationTests
    {
        private const string PackageId = "com.example.valid";
        private const string ProviderAssembly = "Example.Valid.Editor";
        private const string ProviderType = "Example.Valid.Provider";
        private const string ProviderId = "com.example.valid.types";
        private const string OperationId = "com.example.valid.op";
        private const string ExamplePath = "Samples~/Workflows/example.opweave.json";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string RootsDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveExtensionTestRoots");

        private static string RootFor(string packageId) =>
            Path.Combine(RootsDir, packageId.Replace('.', '_'));

        [SetUp]
        public void SetUp()
        {
            if (Directory.Exists(RootsDir))
            {
                Directory.Delete(RootsDir, recursive: true);
            }
            Directory.CreateDirectory(RootsDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(RootsDir))
            {
                Directory.Delete(RootsDir, recursive: true);
            }
        }

        // ---------- manifest / input builders ----------

        private static string Manifest(
            string schemaVersion = "\"1.0\"",
            string extensionId = "\"com.example.valid\"",
            string coreApi = "{\"minInclusive\": \"1.0.0\", \"maxExclusive\": \"2.0.0\"}",
            string unity = "{\"minInclusive\": \"6000.0\"}",
            string assemblies = "[\"Example.Valid.Editor\"]",
            string operations = "[{\"id\": \"com.example.valid.op\", \"version\": 1}]",
            string typeProviders = "[{\"id\": \"com.example.valid.types\", \"assembly\": \"Example.Valid.Editor\"}]",
            string examples = "[\"Samples~/Workflows/example.opweave.json\"]",
            string blueprints = "[]")
        {
            return
                "{\n" +
                "  \"schemaVersion\": " + schemaVersion + ",\n" +
                "  \"extensionId\": " + extensionId + ",\n" +
                "  \"displayName\": \"Valid Extension\",\n" +
                "  \"coreApi\": " + coreApi + ",\n" +
                "  \"unity\": " + unity + ",\n" +
                "  \"assemblies\": " + assemblies + ",\n" +
                "  \"operations\": " + operations + ",\n" +
                "  \"typeProviders\": " + typeProviders + ",\n" +
                "  \"examples\": " + examples + ",\n" +
                "  \"blueprints\": " + blueprints + "\n" +
                "}\n";
        }

        private static string WritePackageRoot(string packageId, bool withExample)
        {
            var root = RootFor(packageId);
            if (withExample)
            {
                var sample = Path.Combine(root, "Samples~", "Workflows");
                Directory.CreateDirectory(sample);
                File.WriteAllText(Path.Combine(sample, "example.opweave.json"), "{\"schemaVersion\":\"1.0\"}\n");
            }
            else
            {
                Directory.CreateDirectory(root);
            }
            return root;
        }

        private static ExtensionValidationInput Input(
            string packageId = PackageId,
            string? manifestText = null,
            bool hasManifest = true,
            IEnumerable<string>? packageAssemblies = null,
            IEnumerable<DiscoveredTypeProvider>? providers = null,
            IEnumerable<ObservedExtensionOperation>? operations = null,
            IEnumerable<ObservedExtensionType>? types = null,
            string? unityVersion = null,
            bool withExample = true)
        {
            return new ExtensionValidationInput(
                packageId,
                WritePackageRoot(packageId, withExample),
                hasManifest ? manifestText ?? Manifest() : null,
                packageAssemblies ?? new[] { ProviderAssembly },
                providers ?? new[] { new DiscoveredTypeProvider(ProviderType, ProviderAssembly) },
                operations ?? new[] { new ObservedExtensionOperation(OperationId, 1, ProviderAssembly) },
                types ?? Array.Empty<ObservedExtensionType>(),
                unityVersion ?? Application.unityVersion);
        }

        private static void AssertAccepted(ExtensionValidationOutcome outcome)
        {
            Assert.That(outcome.Accepted, Is.True,
                () => string.Join("; ", outcome.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        private static void AssertRejected(ExtensionValidationOutcome outcome, params string[] codes)
        {
            Assert.That(outcome.Accepted, Is.False, "extension must be rejected entirely");
            Assert.That(outcome.Diagnostics, Is.Not.Empty, "a rejection must record diagnostics");
            Assert.That(outcome.TypeProviders, Is.Empty, "a rejected extension binds no providers");
            if (codes.Length > 0)
            {
                Assert.That(outcome.Diagnostics.Select(d => d.Code), Has.All.EqualTo(codes[0]));
            }
        }

        // ---------- valid manifest ----------

        [Test]
        public void ValidManifest_IsAcceptedAndBindsProvider()
        {
            var outcome = ExtensionManifestValidator.Validate(Input());

            AssertAccepted(outcome);
            Assert.That(outcome.Manifest, Is.Not.Null);
            Assert.That(outcome.Manifest!.ExtensionId, Is.EqualTo("com.example.valid"));
            Assert.That(outcome.Manifest.CoreApi.Contains(CoreApi.Current), Is.True);
            Assert.That(outcome.TypeProviders, Has.Count.EqualTo(1));
            Assert.That(outcome.TypeProviders[0].ProviderId, Is.EqualTo(ProviderId));
            Assert.That(outcome.TypeProviders[0].TypeFullName, Is.EqualTo(ProviderType));
            Assert.That(outcome.TypeProviders[0].AssemblyName, Is.EqualTo(ProviderAssembly));
        }

        [Test]
        public void Reader_ParsesEveryDeclaredField()
        {
            var result = ExtensionManifestReader.Parse(Manifest());

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var manifest = result.Value!;
            Assert.That(manifest.SchemaVersion, Is.EqualTo("1.0"));
            Assert.That(manifest.ExtensionId, Is.EqualTo("com.example.valid"));
            Assert.That(manifest.DisplayName, Is.EqualTo("Valid Extension"));
            Assert.That(manifest.UnityMinInclusive, Is.EqualTo("6000.0"));
            Assert.That(manifest.Assemblies, Is.EqualTo(new[] { ProviderAssembly }));
            Assert.That(manifest.Operations, Is.EqualTo(new[] { new ExtensionOperationRef(OperationId, 1) }));
            Assert.That(manifest.TypeProviders, Is.EqualTo(new[] { new ExtensionTypeProviderRef(ProviderId, ProviderAssembly) }));
            Assert.That(manifest.Examples, Is.EqualTo(new[] { ExamplePath }));
            Assert.That(manifest.Blueprints, Is.Empty);
        }

        [Test]
        public void ValidManifest_MinimalKeys_IsAccepted()
        {
            var json =
                "{\n" +
                "  \"schemaVersion\": \"1.0\",\n" +
                "  \"extensionId\": \"com.example.minimal\",\n" +
                "  \"coreApi\": {\"minInclusive\": \"1.0.0\", \"maxExclusive\": \"2.0.0\"},\n" +
                "  \"unity\": {\"minInclusive\": \"6000.0\"}\n" +
                "}\n";

            var outcome = ExtensionManifestValidator.Validate(Input(
                packageId: "com.example.minimal",
                manifestText: json,
                packageAssemblies: Array.Empty<string>(),
                providers: Array.Empty<DiscoveredTypeProvider>(),
                operations: Array.Empty<ObservedExtensionOperation>(),
                withExample: false));

            AssertAccepted(outcome);
        }

        // ---------- JSON shape / schemaVersion / extensionId ----------

        [Test]
        public void MalformedJson_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(
                Input(manifestText: "{ \"schemaVersion\": \"1.0\", "));
            AssertRejected(outcome, "");
        }

        [Test]
        public void NonObjectRoot_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(manifestText: "[]"));
            AssertRejected(outcome, "");
        }

        [Test]
        public void SchemaVersion_Unsupported_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(
                Input(manifestText: Manifest(schemaVersion: "\"2.0\"")));
            AssertRejected(outcome, "");
            Assert.That(outcome.Diagnostics[0].JsonPath, Is.EqualTo("$.schemaVersion"));
        }

        [Test]
        public void SchemaVersion_Missing_IsRejected()
        {
            var json = Manifest().Replace("  \"schemaVersion\": \"1.0\",\n", "");
            var outcome = ExtensionManifestValidator.Validate(Input(manifestText: json));
            AssertRejected(outcome, "");
        }

        [Test]
        public void ExtensionId_Reserved_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(
                Input(manifestText: Manifest(extensionId: "\"core.example\"")));
            AssertRejected(outcome, "");
        }

        [Test]
        public void ExtensionId_Malformed_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(
                Input(manifestText: Manifest(extensionId: "\"NOT AN ID\"")));
            AssertRejected(outcome, "");
        }

        [Test]
        public void ExtensionId_Missing_IsRejected()
        {
            var json = Manifest().Replace("  \"extensionId\": \"com.example.valid\",\n", "");
            var outcome = ExtensionManifestValidator.Validate(Input(manifestText: json));
            AssertRejected(outcome, "");
        }

        // ---------- coreApi → OW-OP-012 ----------

        [Test]
        public void CoreApi_Missing_IsRejected012()
        {
            var json = Manifest().Replace("  \"coreApi\": {\"minInclusive\": \"1.0.0\", \"maxExclusive\": \"2.0.0\"},\n", "");
            var outcome = ExtensionManifestValidator.Validate(Input(manifestText: json));
            AssertRejected(outcome, ErrorCodes.OW_OP_012);
            Assert.That(outcome.Diagnostics[0].Hint, Does.Contain(CoreApi.Version));
        }

        [Test]
        public void CoreApi_MissingBound_IsRejected012()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(coreApi: "{\"minInclusive\": \"1.0.0\"}")));
            AssertRejected(outcome, ErrorCodes.OW_OP_012);
            Assert.That(outcome.Diagnostics[0].Hint, Does.Contain("1.0.0").And.Contain("<missing>"));
        }

        [Test]
        public void CoreApi_MalformedBound_IsRejected012()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(coreApi: "{\"minInclusive\": \"1.0\", \"maxExclusive\": \"2.0.0\"}")));
            AssertRejected(outcome, ErrorCodes.OW_OP_012);
            Assert.That(outcome.Diagnostics[0].Hint, Does.Contain("'1.0'..'2.0.0'").And.Contain(CoreApi.Version));
        }

        [Test]
        public void CoreApi_MinNotBelowMax_IsRejected012()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(coreApi: "{\"minInclusive\": \"2.0.0\", \"maxExclusive\": \"1.0.0\"}")));
            AssertRejected(outcome, ErrorCodes.OW_OP_012);
        }

        [Test]
        public void CoreApi_CurrentBelowRange_IsRejected012()
        {
            // [0.9.0, 0.9.9) is well-formed but does not contain 1.0.0.
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(coreApi: "{\"minInclusive\": \"0.9.0\", \"maxExclusive\": \"0.9.9\"}")));
            AssertRejected(outcome, ErrorCodes.OW_OP_012);
            Assert.That(outcome.Diagnostics[0].Hint, Does.Contain("'0.9.0'..'0.9.9'").And.Contain(CoreApi.Version));
        }

        [Test]
        public void CoreApi_CurrentAboveRange_IsRejected012()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(coreApi: "{\"minInclusive\": \"2.0.0\", \"maxExclusive\": \"3.0.0\"}")));
            AssertRejected(outcome, ErrorCodes.OW_OP_012);
        }

        // ---------- unity minimum ----------

        [Test]
        public void Unity_Missing_IsRejected()
        {
            var json = Manifest().Replace("  \"unity\": {\"minInclusive\": \"6000.0\"},\n", "");
            var outcome = ExtensionManifestValidator.Validate(Input(manifestText: json));
            AssertRejected(outcome, "");
        }

        [Test]
        public void Unity_Malformed_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(unity: "{\"minInclusive\": \"six\"}")));
            AssertRejected(outcome, "");
        }

        [Test]
        public void Unity_AboveCurrent_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(unity: "{\"minInclusive\": \"9999.0\"}")));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics[0].Message, Does.Contain("9999.0"));
        }

        [Test]
        public void Unity_BelowCurrent_IsAccepted()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(unity: "{\"minInclusive\": \"0.1\"}")));
            AssertAccepted(outcome);
        }

        // ---------- assemblies / providers / operations ----------

        [Test]
        public void Assemblies_DeclaredMissing_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                packageAssemblies: Array.Empty<string>(),
                providers: Array.Empty<DiscoveredTypeProvider>(),
                operations: Array.Empty<ObservedExtensionOperation>()));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics[0].Message, Does.Contain(ProviderAssembly));
        }

        [Test]
        public void Assemblies_UndeclaredProviderAssembly_IsRejected()
        {
            // A provider class in a package assembly the manifest does not
            // declare is a declaration/reality mismatch.
            var outcome = ExtensionManifestValidator.Validate(Input(
                packageAssemblies: new[] { ProviderAssembly, "Example.Extra.Editor" },
                providers: new[]
                {
                    new DiscoveredTypeProvider(ProviderType, ProviderAssembly),
                    new DiscoveredTypeProvider("Example.Extra.Provider", "Example.Extra.Editor"),
                }));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("undeclared assembly"));
        }

        [Test]
        public void Operations_DeclaredNotFound_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                operations: Array.Empty<ObservedExtensionOperation>()));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics[0].Hint, Does.Contain(OperationId + "@1"));
        }

        [Test]
        public void Operations_FoundNotDeclared_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(operations: "[]"),
                operations: new[] { new ObservedExtensionOperation("com.example.other.op", 2, ProviderAssembly) }));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics[0].Hint, Does.Contain("com.example.other.op@2"));
        }

        [Test]
        public void Operations_UndeclaredAssembly_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                packageAssemblies: new[] { ProviderAssembly, "Example.Extra.Editor" },
                operations: new[]
                {
                    new ObservedExtensionOperation(OperationId, 1, ProviderAssembly),
                    new ObservedExtensionOperation("com.example.other.op", 1, "Example.Extra.Editor"),
                },
                manifestText: Manifest(
                    operations: "[{\"id\": \"com.example.valid.op\", \"version\": 1}, {\"id\": \"com.example.other.op\", \"version\": 1}]")));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("undeclared assembly"));
        }

        [Test]
        public void Operations_DuplicateDeclared_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(
                    operations: "[{\"id\": \"com.example.valid.op\", \"version\": 1}, {\"id\": \"com.example.valid.op\", \"version\": 1}]")));
            AssertRejected(outcome, "");
            Assert.That(outcome.Diagnostics[0].Message, Does.Contain("duplicate"));
        }

        [Test]
        public void Operations_ReservedId_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(
                    operations: "[{\"id\": \"unity.fake.op\", \"version\": 1}]"),
                operations: new[] { new ObservedExtensionOperation("unity.fake.op", 1, ProviderAssembly) }));
            AssertRejected(outcome, ErrorCodes.OW_OP_007);
        }

        [Test]
        public void Operations_VersionAboveInt32_IsRejectedNotTruncated()
        {
            // 4294967297 truncates to 1 in an unchecked 32-bit cast; it must
            // be rejected as out of range, never equated with a version-1
            // Descriptor.
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(
                    operations: "[{\"id\": \"com.example.valid.op\", \"version\": 4294967297}]")));
            AssertRejected(outcome, "");
            Assert.That(outcome.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("'version'"));
        }

        [Test]
        public void Operations_VersionAboveInt64_IsRejected()
        {
            // Beyond Int64 the canonical JSON reader refuses the literal
            // itself — the extension is still rejected with a diagnostic,
            // never by a conversion exception.
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(
                    operations: "[{\"id\": \"com.example.valid.op\", \"version\": 99999999999999999999999}]")));
            AssertRejected(outcome, "");
        }

        [Test]
        public void Operations_VersionInt32Max_IsAcceptedByReader()
        {
            var result = ExtensionManifestReader.Parse(Manifest(
                operations: "[{\"id\": \"com.example.valid.op\", \"version\": 2147483647}]"));
            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(result.Value!.Operations,
                Is.EqualTo(new[] { new ExtensionOperationRef(OperationId, int.MaxValue) }));
        }

        [Test]
        public void TypeProviders_NotFoundInAssembly_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                providers: Array.Empty<DiscoveredTypeProvider>()));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics[0].Message, Does.Contain("No [OpWeaveTypeProvider] class"));
        }

        [Test]
        public void TypeProviders_AssemblyNotDeclared_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(
                    typeProviders: "[{\"id\": \"com.example.valid.types\", \"assembly\": \"Example.Other.Editor\"}]")));
            AssertRejected(outcome, "");
            Assert.That(outcome.Diagnostics[0].Message, Does.Contain("not listed in 'assemblies'"));
        }

        [Test]
        public void TypeProviders_UndeclaredClass_IsRejected()
        {
            // Two provider classes in the declared assembly but only one
            // declared Provider ID.
            var outcome = ExtensionManifestValidator.Validate(Input(
                providers: new[]
                {
                    new DiscoveredTypeProvider(ProviderType, ProviderAssembly),
                    new DiscoveredTypeProvider("Example.Valid.ProviderB", ProviderAssembly),
                }));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics[0].Message, Does.Contain("2 [OpWeaveTypeProvider]"));
        }

        [Test]
        public void TypeProviders_DuplicateId_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(
                    typeProviders: "[{\"id\": \"com.example.valid.types\", \"assembly\": \"Example.Valid.Editor\"}, {\"id\": \"com.example.valid.types\", \"assembly\": \"Example.Valid.Editor\"}]")));
            AssertRejected(outcome, "");
            Assert.That(outcome.Diagnostics[0].Message, Does.Contain("duplicate Provider ID"));
        }

        [Test]
        public void TypeProviders_TwoClasses_TwoIds_BindsDeterministically()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                providers: new[]
                {
                    new DiscoveredTypeProvider("Example.Valid.ProviderB", ProviderAssembly),
                    new DiscoveredTypeProvider(ProviderType, ProviderAssembly),
                },
                manifestText: Manifest(
                    typeProviders: "[{\"id\": \"com.example.valid.b\", \"assembly\": \"Example.Valid.Editor\"}, {\"id\": \"com.example.valid.a\", \"assembly\": \"Example.Valid.Editor\"}]")));

            AssertAccepted(outcome);
            Assert.That(outcome.TypeProviders, Has.Count.EqualTo(2));
            // Classes sorted by full name, entries sorted by Provider ID.
            Assert.That(outcome.TypeProviders[0].ProviderId, Is.EqualTo("com.example.valid.a"));
            Assert.That(outcome.TypeProviders[0].TypeFullName, Is.EqualTo("Example.Valid.Provider"));
            Assert.That(outcome.TypeProviders[1].ProviderId, Is.EqualTo("com.example.valid.b"));
            Assert.That(outcome.TypeProviders[1].TypeFullName, Is.EqualTo("Example.Valid.ProviderB"));
        }

        // ---------- offered Type IDs (SPEC §X.3.2, §B0.1) ----------

        [Test]
        public void ObservedTypes_ValidTypeId_IsAccepted()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                types: new[] { new ObservedExtensionType("com.example.valid.type", ProviderAssembly) }));
            AssertAccepted(outcome);
        }

        [Test]
        public void ObservedTypes_ReservedTypeId_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                types: new[] { new ObservedExtensionType("core.fake.type", ProviderAssembly) }));
            AssertRejected(outcome, ErrorCodes.OW_OP_007);
            Assert.That(outcome.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("reserved"));
        }

        [Test]
        public void ObservedTypes_MalformedTypeId_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                types: new[] { new ObservedExtensionType("not a type", ProviderAssembly) }));
            AssertRejected(outcome, "");
            Assert.That(outcome.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("canonical"));
        }

        [Test]
        public void ObservedTypes_DuplicateWithinExtension_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                types: new[]
                {
                    new ObservedExtensionType("com.example.valid.type", ProviderAssembly),
                    new ObservedExtensionType("com.example.valid.type", ProviderAssembly),
                }));
            AssertRejected(outcome, ErrorCodes.OW_OP_013);
            Assert.That(outcome.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("more than once"));
        }

        [Test]
        public void ObservedTypes_UndeclaredAssembly_IsRejected()
        {
            // An offered Type ID from a package assembly the manifest does
            // not declare is a declaration/reality mismatch.
            var outcome = ExtensionManifestValidator.Validate(Input(
                packageAssemblies: new[] { ProviderAssembly, "Example.Extra.Editor" },
                types: new[] { new ObservedExtensionType("com.example.valid.type", "Example.Extra.Editor") }));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("undeclared assembly"));
        }

        // ---------- examples ----------

        [Test]
        public void Examples_MissingFile_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(examples: "[\"Samples~/Workflows/absent.opweave.json\"]")));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
            Assert.That(outcome.Diagnostics[0].Message, Does.Contain("absent.opweave.json"));
        }

        [Test]
        public void Examples_EscapedPath_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(examples: "[\"../escape.opweave.json\"]")));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
        }

        [Test]
        public void Examples_RootedPath_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(examples: "[\"/abs/opweave.json\"]")));
            AssertRejected(outcome, ErrorCodes.OW_OP_018);
        }

        // ---------- blueprints → OW-OP-010 ----------

        [Test]
        public void Blueprints_NonEmpty_IsRejected010()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                manifestText: Manifest(blueprints: "[\"Blueprints/setup.opweave-blueprint.json\"]")));
            AssertRejected(outcome, ErrorCodes.OW_OP_010);
        }

        // ---------- manifest presence ----------

        [Test]
        public void NoManifest_WithCandidates_IsRejected()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(hasManifest: false));
            AssertRejected(outcome, "");
            Assert.That(outcome.ManifestPresent, Is.False);
            Assert.That(outcome.Diagnostics[0].Message, Does.Contain("opweave.extension.json"));
        }

        [Test]
        public void NoManifest_WithoutCandidates_IsAcceptedNoOp()
        {
            var outcome = ExtensionManifestValidator.Validate(Input(
                hasManifest: false,
                providers: Array.Empty<DiscoveredTypeProvider>(),
                operations: Array.Empty<ObservedExtensionOperation>()));
            AssertAccepted(outcome);
            Assert.That(outcome.ManifestPresent, Is.False);
            Assert.That(outcome.Manifest, Is.Null);
        }

        // ---------- cross-extension duplicates ----------

        [Test]
        public void ValidateAll_DuplicateExtensionId_RejectsBoth()
        {
            var outcomes = ExtensionManifestValidator.ValidateAll(new[]
            {
                Input(packageId: "com.example.a", manifestText: Manifest(extensionId: "\"com.example.dup\"")),
                Input(packageId: "com.example.b", manifestText: Manifest(extensionId: "\"com.example.dup\"")),
            });

            Assert.That(outcomes, Has.Count.EqualTo(2));
            // Each outcome carries the cross-package verdicts for all
            // colliding declared identities: extensionId and the shared
            // Operation ID are OW-OP-018, the shared Provider ID OW-OP-013.
            var expected = new[]
            {
                ErrorCodes.OW_OP_018, ErrorCodes.OW_OP_018, ErrorCodes.OW_OP_013,
            };
            AssertRejected(outcomes[0]);
            AssertRejected(outcomes[1]);
            Assert.That(outcomes[0].Diagnostics.Select(d => d.Code),
                Is.EquivalentTo(expected));
            Assert.That(outcomes[1].Diagnostics.Select(d => d.Code),
                Is.EquivalentTo(expected));
            Assert.That(outcomes[0].Diagnostics.Select(d => d.Message),
                Has.Some.Contains("extensionId").And.Some.Contains("more than one package"));
        }

        [Test]
        public void ValidateAll_DuplicateOperationId_RejectsBoth()
        {
            // Only the Operation identity collides; the second package keeps
            // a distinct Provider ID and extensionId.
            var outcomes = ExtensionManifestValidator.ValidateAll(new[]
            {
                Input(packageId: "com.example.a"),
                Input(packageId: "com.example.b",
                    manifestText: Manifest(
                        extensionId: "\"com.example.other\"",
                        typeProviders: "[{\"id\": \"com.example.other.types\", \"assembly\": \"Example.Valid.Editor\"}]")),
            });

            AssertRejected(outcomes[0], ErrorCodes.OW_OP_018);
            AssertRejected(outcomes[1], ErrorCodes.OW_OP_018);
            Assert.That(outcomes[0].Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Operation").And.Some.Contains("more than one package"));
        }

        [Test]
        public void ValidateAll_DuplicateProviderId_RejectsBoth()
        {
            // Only the Provider ID collides; the second package keeps a
            // distinct extensionId and Operation identity.
            var outcomes = ExtensionManifestValidator.ValidateAll(new[]
            {
                Input(packageId: "com.example.a"),
                Input(packageId: "com.example.b",
                    manifestText: Manifest(
                        extensionId: "\"com.example.other\"",
                        operations: "[{\"id\": \"com.example.other.op\", \"version\": 1}]"),
                    operations: new[] { new ObservedExtensionOperation("com.example.other.op", 1, ProviderAssembly) }),
            });

            AssertRejected(outcomes[0], ErrorCodes.OW_OP_013);
            AssertRejected(outcomes[1], ErrorCodes.OW_OP_013);
            Assert.That(outcomes[0].Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Provider ID").And.Some.Contains("more than one package"));
        }

        [Test]
        public void ValidateAll_DuplicateTypeId_RejectsBoth()
        {
            // Only the offered Type ID collides; the second package keeps a
            // distinct extensionId, Provider ID and Operation identity.
            var outcomes = ExtensionManifestValidator.ValidateAll(new[]
            {
                Input(packageId: "com.example.a",
                    types: new[] { new ObservedExtensionType("com.example.shared.type", ProviderAssembly) }),
                Input(packageId: "com.example.b",
                    manifestText: Manifest(
                        extensionId: "\"com.example.other\"",
                        operations: "[{\"id\": \"com.example.other.op\", \"version\": 1}]",
                        typeProviders: "[{\"id\": \"com.example.other.types\", \"assembly\": \"Example.Valid.Editor\"}]"),
                    operations: new[] { new ObservedExtensionOperation("com.example.other.op", 1, ProviderAssembly) },
                    types: new[] { new ObservedExtensionType("com.example.shared.type", ProviderAssembly) }),
            });

            AssertRejected(outcomes[0], ErrorCodes.OW_OP_013);
            AssertRejected(outcomes[1], ErrorCodes.OW_OP_013);
            Assert.That(outcomes[0].Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("more than one package"));
        }

        [Test]
        public void ValidateAll_UnrelatedExtension_StaysAccepted()
        {
            var outcomes = ExtensionManifestValidator.ValidateAll(new[]
            {
                Input(packageId: "com.example.a"),
                Input(packageId: "com.example.b",
                    manifestText: Manifest(
                        extensionId: "\"com.example.other\"",
                        operations: "[{\"id\": \"com.example.other.op\", \"version\": 1}]",
                        typeProviders: "[{\"id\": \"com.example.other.types\", \"assembly\": \"Example.Valid.Editor\"}]"),
                    operations: new[] { new ObservedExtensionOperation("com.example.other.op", 1, ProviderAssembly) }),
            });

            AssertAccepted(outcomes[0]);
            AssertAccepted(outcomes[1]);
        }
    }
}
