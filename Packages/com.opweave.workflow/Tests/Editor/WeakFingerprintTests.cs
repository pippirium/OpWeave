#nullable enable
// T-W2.2-02 [Edit] tests: weak implementation fingerprints from the compiled
// bytes of the defining assembly (SPEC §0.1 F-09, §C0.2 weak, §C3 rule 6,
// §B0.2 item 5, §0.6 SG-11).
//
// local:T-W2.2-02:weak-fingerprint — the [UnityTest] rebuilds the Operation
// and Type fingerprint snapshots across a real domain reload (SPEC §T.3:
// RecompileScripts + a domain sentinel; `yield return new
// WaitForDomainReload()` is deliberately not used — on 6000.0.85f1 the
// reload can complete inside RecompileScripts' own wait, after which
// WaitForDomainReload.Perform() throws, the same interleaving caveat
// documented by L06PrefabReloadProbe).
//
// The method-body-edit fixture lives entirely under the test-created folder
// Assets/OpWeaveFingerprintProbe/ (two independent Editor asmdefs); state that
// must cross the reload is written to <project>/Library/OpWeaveFingerprintProbe/
// because UTF locals do not survive a domain reload. Both are removed by
// [UnityTearDown]; nothing tracked is modified.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using ReflectionAssembly = System.Reflection.Assembly;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.2-02:weak-fingerprint — SHA-256 of compiled defining-assembly
    /// bytes, refusal diagnostics for unresolvable assemblies and the
    /// cross-domain-reload change-detection fixtures.
    /// </summary>
    internal sealed class WeakFingerprintTests
    {
        // SPEC §B0.2 fixes this exact string for weak fingerprints; the
        // CoreApi constant itself lands with T-W1.5-01 and callers then pass
        // CoreApi.Version verbatim.
        private const string CoreApiVersionForTests = "1.0.0";

        private const string ProbeAssetDir = "Assets/OpWeaveFingerprintProbe";
        private const string Probe1Assembly = "OpWeave.FingerprintProbe1";
        private const string Probe2Assembly = "OpWeave.FingerprintProbe2";
        private const string EditorFixtureAssembly = "OpWeaveFixtures.Editor";
        private const string PluginFixtureAssembly = "OpWeave.Plugin";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string StateDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveFingerprintProbe");

        private static string BeforeSnapshotFile => Path.Combine(StateDir, "before.txt");

        private static string BeforeTypeSnapshotFile => Path.Combine(StateDir, "types-before.txt");

        private static string ForeignDllPath =>
            Path.Combine(ProjectRoot, "Assets", "OpWeaveFixtures", "Plugins~", "OpWeave.Foreign.dll");

        // ---------- helpers ----------

        private static ReflectionAssembly LoadByName(string name)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);
            if (loaded is not null)
            {
                return loaded;
            }
            return ReflectionAssembly.Load(new AssemblyName(name));
        }

        private static Type LoadType(string assemblyName, string typeName)
        {
            var type = LoadByName(assemblyName).GetType(typeName);
            Assert.That(type, Is.Not.Null, $"fixture type {typeName} missing in {assemblyName}");
            return type!;
        }

        private static string Sha256HexOfFile(string path)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(File.ReadAllBytes(path));
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }

        private static JsonValue MustParse(string json)
        {
            var parsed = JsonValue.Parse(json);
            Assert.That(parsed.Ok, Is.True, "synthetic descriptor JSON must parse");
            return parsed.Value!;
        }

        private static Result<OperationFingerprint> FingerprintOperation(
            string opId, int version, string assemblyName, string typeName)
        {
            var descriptor = MustParse($"{{\"id\":\"{opId}\",\"version\":{version}}}");
            return WeakFingerprintProvider.ForOperation(
                opId, version, descriptor, LoadType(assemblyName, typeName), CoreApiVersionForTests);
        }

        private static void AssertFailure(Result<DefiningAssemblyFingerprint> result) =>
            AssertFailure(result.Ok, result.Diagnostics);

        private static void AssertFailure(Result<OperationFingerprint> result) =>
            AssertFailure(result.Ok, result.Diagnostics);

        private static void AssertFailure(Result<TypeFingerprint> result) =>
            AssertFailure(result.Ok, result.Diagnostics);

        private static void AssertFailure(bool ok, IReadOnlyList<Diagnostic> diagnostics)
        {
            Assert.That(ok, Is.False);
            Assert.That(diagnostics, Is.Not.Empty, "a refusal must record diagnostics");
            Assert.That(diagnostics.Select(d => d.Code), Has.All.EqualTo(ErrorCodes.OW_OP_017));
        }

        // ---------- compiled-bytes hashing ----------

        [Test]
        public void ForDefiningType_PackageAssembly_MatchesFileBytesSha256()
        {
            var result = WeakFingerprintProvider.ForDefiningType(typeof(WeakFingerprintProvider));

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var fingerprint = result.Value!;
            Assert.That(fingerprint.DefiningAssemblyName, Is.EqualTo("OpWeave.UnityAdapter"));
            Assert.That(fingerprint.DefiningAssemblySha256, Does.Match("^[0-9a-f]{64}$"));
            // The hash is of the exact compiled bytes on disk — not a source
            // hash and not the MVID (SPEC §C3 rule 6).
            var path = EditorAssemblyResolver.ResolveForType(typeof(WeakFingerprintProvider))
                .Value!.CompiledAssemblyPath;
            Assert.That(fingerprint.DefiningAssemblySha256, Is.EqualTo(Sha256HexOfFile(path)));
        }

        [Test]
        public void ForDefiningType_EditorFixtureAssembly_Resolves()
        {
            var result = WeakFingerprintProvider.ForDefiningType(
                LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker"));

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(result.Value!.DefiningAssemblyName, Is.EqualTo(EditorFixtureAssembly));
        }

        [Test]
        public void ForDefiningType_PrecompiledPlugin_Resolves()
        {
            var result = WeakFingerprintProvider.ForDefiningType(
                LoadType(PluginFixtureAssembly, "OpWeave.Fixtures.OpWeave_PluginMarker"));

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Message)));
            var fingerprint = result.Value!;
            Assert.That(fingerprint.DefiningAssemblyName, Is.EqualTo(PluginFixtureAssembly));
            Assert.That(fingerprint.DefiningAssemblySha256, Is.EqualTo(Sha256HexOfFile(
                LoadByName(PluginFixtureAssembly).Location)));
        }

        [Test]
        public void ForDefiningType_ImplementationHashIsNotSourceHash()
        {
            // SPEC §C3 rule 6: source-file content never substitutes the
            // compiled bytes.
            var result = WeakFingerprintProvider.ForDefiningType(
                LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker"));
            Assert.That(result.Ok, Is.True);
            var sourcePath = Path.Combine(
                ProjectRoot, "Assets", "OpWeaveFixtures", "Editor", "EditorFixtureMarker.cs");
            Assert.That(File.Exists(sourcePath), Is.True);
            Assert.That(result.Value!.DefiningAssemblySha256,
                Is.Not.EqualTo(Sha256HexOfFile(sourcePath)));
        }

        // ---------- unresolvable defining assembly → refusal recorded ----------

        [Test]
        public void ForDefiningType_UnresolvableForeign_Fails017()
        {
            Assert.That(File.Exists(ForeignDllPath), Is.True, "fixture DLL missing: " + ForeignDllPath);
            var type = ReflectionAssembly.LoadFrom(ForeignDllPath)
                .GetType("OpWeave.Fixtures.OpWeave_ForeignMarker");
            Assert.That(type, Is.Not.Null);

            AssertFailure(WeakFingerprintProvider.ForDefiningType(type!));
        }

        [Test]
        public void ForDefiningType_ByteLoadedAssembly_Fails017()
        {
            var fixture = ReflectionAssembly.Load(File.ReadAllBytes(ForeignDllPath));

            AssertFailure(WeakFingerprintProvider.ForDefiningType(
                fixture.GetType("OpWeave.Fixtures.OpWeave_ForeignMarker")!));
        }

        [Test]
        public void ForProvenance_UnreadableBytes_Fails017WithHint()
        {
            var missing = Path.Combine(ProjectRoot, "Library", "no-such-compiled-assembly.dll");
            var provenance = new EditorAssemblyProvenance(
                "OpWeave.Missing", missing, false, OperationOrigin.Custom, null, null);

            var result = WeakFingerprintProvider.ForProvenance(provenance);

            AssertFailure(result);
            Assert.That(result.Diagnostics[0].Message, Does.Contain("compiled bytes"));
            Assert.That(result.Diagnostics[0].Hint, Does.Contain("OpWeave.Missing"));
        }

        [Test]
        public void ForOperation_UnresolvableImplementation_Fails017()
        {
            var type = ReflectionAssembly.Load(File.ReadAllBytes(ForeignDllPath))
                .GetType("OpWeave.Fixtures.OpWeave_ForeignMarker")!;

            var result = WeakFingerprintProvider.ForOperation(
                "foreign.op", 1, MustParse("{\"id\":\"foreign.op\"}"), type, CoreApiVersionForTests);

            AssertFailure(result);
        }

        // ---------- used-Operation / used-Type fingerprint records ----------

        [Test]
        public void ForOperation_ProducesSpecFieldsAndCanonicalJson()
        {
            var descriptor = MustParse("{\"id\":\"fixture.op\",\"version\":1}");
            var impl = WeakFingerprintProvider.ForDefiningType(
                LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker"));
            Assert.That(impl.Ok, Is.True);

            var result = WeakFingerprintProvider.ForOperation(
                "fixture.op", 1, descriptor,
                LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker"),
                CoreApiVersionForTests);

            Assert.That(result.Ok, Is.True);
            var fingerprint = result.Value!;
            Assert.That(fingerprint.Id, Is.EqualTo("fixture.op"));
            Assert.That(fingerprint.Version, Is.EqualTo(1));
            // descriptorHash = SHA-256 of the Descriptor's canonical JSON (SPEC §C3 rule 6).
            Assert.That(fingerprint.DescriptorHash, Is.EqualTo(descriptor.Sha256));
            Assert.That(fingerprint.ImplementationHash, Is.EqualTo(impl.Value!.DefiningAssemblySha256));
            Assert.That(fingerprint.CoreApiVersion, Is.EqualTo("1.0.0"));
            Assert.That(fingerprint.ToJsonValue().CanonicalText, Is.EqualTo(
                "{\"coreApiVersion\":\"1.0.0\",\"descriptorHash\":\"" + descriptor.Sha256 +
                "\",\"id\":\"fixture.op\",\"implementationHash\":\"" +
                impl.Value!.DefiningAssemblySha256 + "\",\"version\":1}"));
        }

        [Test]
        public void ForOperation_DescriptorChange_KeepsImplementationHash()
        {
            var type = LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker");
            var first = WeakFingerprintProvider.ForOperation(
                "fixture.op", 1, MustParse("{\"id\":\"fixture.op\",\"version\":1}"),
                type, CoreApiVersionForTests);
            var second = WeakFingerprintProvider.ForOperation(
                "fixture.op", 1, MustParse("{\"id\":\"fixture.op\",\"version\":2}"),
                type, CoreApiVersionForTests);

            Assert.That(first.Ok && second.Ok, Is.True);
            Assert.That(first.Value!.DescriptorHash, Is.Not.EqualTo(second.Value!.DescriptorHash));
            Assert.That(first.Value!.ImplementationHash, Is.EqualTo(second.Value!.ImplementationHash));
            Assert.That(first.Value!.ToJsonValue().CanonicalText,
                Is.Not.EqualTo(second.Value!.ToJsonValue().CanonicalText));
        }

        [Test]
        public void ForType_ProducesSpecFieldsAndCanonicalJson()
        {
            var typeDefinition = MustParse("{\"typeId\":\"test.fake\",\"kind\":\"primitive\"}");
            var impl = WeakFingerprintProvider.ForDefiningType(
                LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker"));
            Assert.That(impl.Ok, Is.True);

            var result = WeakFingerprintProvider.ForType(
                "test.fake", "test.provider", typeDefinition,
                LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker"),
                CoreApiVersionForTests);

            Assert.That(result.Ok, Is.True);
            var fingerprint = result.Value!;
            Assert.That(fingerprint.TypeId, Is.EqualTo("test.fake"));
            Assert.That(fingerprint.ProviderId, Is.EqualTo("test.provider"));
            Assert.That(fingerprint.TypeDefinitionHash, Is.EqualTo(typeDefinition.Sha256));
            Assert.That(fingerprint.Assembly, Is.EqualTo(impl.Value!));
            Assert.That(fingerprint.ToJsonValue().CanonicalText, Is.EqualTo(
                "{\"coreApiVersion\":\"1.0.0\",\"definingAssemblyName\":\"" + EditorFixtureAssembly +
                "\",\"definingAssemblySha256\":\"" + impl.Value!.DefiningAssemblySha256 +
                "\",\"providerId\":\"test.provider\",\"typeDefinitionHash\":\"" +
                typeDefinition.Sha256 + "\",\"typeId\":\"test.fake\"}"));
        }

        [Test]
        public void ForType_UnresolvableProvider_Fails017()
        {
            var type = ReflectionAssembly.Load(File.ReadAllBytes(ForeignDllPath))
                .GetType("OpWeave.Fixtures.OpWeave_ForeignMarker")!;

            var result = WeakFingerprintProvider.ForType(
                "test.fake", "test.provider", MustParse("{\"typeId\":\"test.fake\"}"),
                type, CoreApiVersionForTests);

            AssertFailure(result);
        }

        [Test]
        public void DefiningAssemblyFingerprint_JsonUsesSg11FieldNames()
        {
            var fingerprint = new DefiningAssemblyFingerprint("OpWeave.X", new string('a', 64));

            Assert.That(fingerprint.ToJsonValue().CanonicalText, Is.EqualTo(
                "{\"definingAssemblyName\":\"OpWeave.X\",\"definingAssemblySha256\":\"" +
                new string('a', 64) + "\"}"));
        }

        // ---------- validation: no guessed identifiers ----------

        [Test]
        public void ForOperation_RejectsEmptyIdentifiers()
        {
            var type = LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker");
            var descriptor = MustParse("{\"id\":\"x\"}");

            Assert.Throws<ArgumentNullException>(() =>
                WeakFingerprintProvider.ForOperation(null!, 1, descriptor, type, CoreApiVersionForTests));
            Assert.Throws<ArgumentException>(() =>
                WeakFingerprintProvider.ForOperation("", 1, descriptor, type, CoreApiVersionForTests));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                WeakFingerprintProvider.ForOperation("x", 0, descriptor, type, CoreApiVersionForTests));
            Assert.Throws<ArgumentNullException>(() =>
                WeakFingerprintProvider.ForOperation("x", 1, null!, type, CoreApiVersionForTests));
            Assert.Throws<ArgumentNullException>(() =>
                WeakFingerprintProvider.ForOperation("x", 1, descriptor, null!, CoreApiVersionForTests));
            Assert.Throws<ArgumentException>(() =>
                WeakFingerprintProvider.ForOperation("x", 1, descriptor, type, ""));
        }

        [Test]
        public void ForType_RejectsEmptyIdentifiers()
        {
            var type = LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker");
            var definition = MustParse("{\"typeId\":\"t\"}");

            Assert.Throws<ArgumentException>(() =>
                WeakFingerprintProvider.ForType("", "p", definition, type, CoreApiVersionForTests));
            Assert.Throws<ArgumentException>(() =>
                WeakFingerprintProvider.ForType("t", "", definition, type, CoreApiVersionForTests));
            Assert.Throws<ArgumentNullException>(() =>
                WeakFingerprintProvider.ForType("t", "p", null!, type, CoreApiVersionForTests));
            Assert.Throws<ArgumentNullException>(() =>
                WeakFingerprintProvider.ForType("t", "p", definition, null!, CoreApiVersionForTests));
            Assert.Throws<ArgumentException>(() =>
                WeakFingerprintProvider.ForType("t", "p", definition, type, ""));
        }

        // ---------- stability across rebuilds ----------

        [Test]
        public void Fingerprint_IdenticalAcrossRecomputations()
        {
            var type = LoadType(EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker");
            var descriptor = MustParse("{\"id\":\"fixture.op\",\"version\":1}");

            var first = WeakFingerprintProvider.ForOperation("fixture.op", 1, descriptor, type, CoreApiVersionForTests);
            var second = WeakFingerprintProvider.ForOperation("fixture.op", 1, descriptor, type, CoreApiVersionForTests);

            Assert.That(first.Ok && second.Ok, Is.True);
            Assert.That(second.Value, Is.EqualTo(first.Value));
            Assert.That(second.Value!.ToJsonValue().CanonicalText,
                Is.EqualTo(first.Value!.ToJsonValue().CanonicalText));
        }

        // ---------- local:T-W2.2-02:weak-fingerprint (domain reload) ----------

        // Domain sentinel: a static field is reset by a domain reload, so a
        // non-null value proves we still run in the old domain.
        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private static bool CleanupProbeArtifacts()
        {
            var dirty = false;
            if (Directory.Exists(StateDir))
            {
                Directory.Delete(StateDir, recursive: true);
            }
            if (AssetDatabase.IsValidFolder(ProbeAssetDir))
            {
                AssetDatabase.DeleteAsset(ProbeAssetDir);
                dirty = true;
            }
            var onDisk = Path.Combine(ProjectRoot, "Assets", "OpWeaveFingerprintProbe");
            if (Directory.Exists(onDisk))
            {
                Directory.Delete(onDisk, recursive: true);
                dirty = true;
            }
            return dirty;
        }

        private static void WriteProbeAssets(bool secondVariant)
        {
            var root = Path.Combine(ProjectRoot, "Assets", "OpWeaveFingerprintProbe");
            Directory.CreateDirectory(Path.Combine(root, "Probe1"));
            Directory.CreateDirectory(Path.Combine(root, "Probe2"));

            const string asmdefTemplate =
                "{{\n    \"name\": \"{0}\",\n    \"rootNamespace\": \"OpWeave.FingerprintProbes\",\n" +
                "    \"references\": [],\n    \"includePlatforms\": [\n        \"Editor\"\n    ],\n" +
                "    \"excludePlatforms\": [],\n    \"allowUnsafeCode\": false,\n" +
                "    \"autoReferenced\": false,\n    \"noEngineReferences\": false\n}}\n";
            File.WriteAllText(
                Path.Combine(root, "Probe1", Probe1Assembly + ".asmdef"),
                string.Format(asmdefTemplate, Probe1Assembly));
            File.WriteAllText(
                Path.Combine(root, "Probe2", Probe2Assembly + ".asmdef"),
                string.Format(asmdefTemplate, Probe2Assembly));

            WriteProbeOpsFile(Path.Combine(root, "Probe1", "ProbeOps.cs"), secondVariant);
            File.WriteAllText(Path.Combine(root, "Probe1", "ProbeExtra.cs"),
                "#nullable enable\nnamespace OpWeave.FingerprintProbes\n{\n" +
                "    public static class ProbeExtra\n    {\n" +
                "        public static string Tag() => \"extra\";\n    }\n" +
                "    public static class ProbeProviderExtra\n    {\n" +
                "        public static string Kind() => \"provider-extra\";\n    }\n}\n");
            File.WriteAllText(Path.Combine(root, "Probe2", "ProbeOps2.cs"),
                "#nullable enable\nnamespace OpWeave.FingerprintProbes\n{\n" +
                "    public static class ProbeGamma\n    {\n" +
                "        public static string Tag() => \"gamma\";\n    }\n" +
                "    public static class ProbeProviderGamma\n    {\n" +
                "        public static string Kind() => \"provider-gamma\";\n    }\n}\n");
        }

        // Two variants differing only inside method bodies (SPEC §C3 test:
        // changing only an Execute body must change implementationHash).
        private static void WriteProbeOpsFile(string path, bool secondVariant)
        {
            var marker = secondVariant ? "v2" : "v1";
            File.WriteAllText(path,
                "#nullable enable\nnamespace OpWeave.FingerprintProbes\n{\n" +
                "    public static class ProbeOpAlpha\n    {\n" +
                $"        public static string Body() => \"alpha-{marker}\";\n    }}\n" +
                "    public static class ProbeOpBeta\n    {\n" +
                $"        public static string Body() => \"beta-{marker}\";\n    }}\n" +
                "    public static class ProbeProviderAlpha\n    {\n" +
                "        public static string Kind() => \"provider-alpha\";\n    }\n}\n");
        }

        // (assemblyName, typeFullName, synthetic operation id, changedByEdit)
        private static readonly (string Assembly, string Type, string OpId, bool ChangedByEdit)[] s_candidates =
        {
            (Probe1Assembly, "OpWeave.FingerprintProbes.ProbeOpAlpha", "proj.probe.alpha", true),
            (Probe1Assembly, "OpWeave.FingerprintProbes.ProbeOpBeta", "proj.probe.beta", true),
            (Probe1Assembly, "OpWeave.FingerprintProbes.ProbeExtra", "proj.probe.extra", true),
            (Probe2Assembly, "OpWeave.FingerprintProbes.ProbeGamma", "proj.probe.gamma", false),
            (EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker", "fixture.editor.marker", false),
            (PluginFixtureAssembly, "OpWeave.Fixtures.OpWeave_PluginMarker", "fixture.plugin.marker", false),
            ("OpWeave.UnityAdapter", "OpWeave.UnityAdapter.WeakFingerprintProvider", "std.weakprovider", false),
            ("OpWeave.UnityAdapter.Tests", "OpWeave.UnityAdapter.Tests.WeakFingerprintTests", "std.weaktests", false),
        };

        // Type-definition subjects fingerprinted through
        // WeakFingerprintProvider.ForType: (provider's assemblyName,
        // providerTypeFullName, synthetic type id, synthetic provider id,
        // changedByEdit). Both probe asmdefs contribute provider types, so
        // the edited assembly's Type fingerprints and the unchanged
        // assembly's are exercised across the same reload as the Operation
        // fingerprints above.
        private static readonly (string Assembly, string ProviderType, string TypeId, string ProviderId, bool ChangedByEdit)[] s_typeCandidates =
        {
            (Probe1Assembly, "OpWeave.FingerprintProbes.ProbeProviderAlpha", "probe.type.alpha", "probe.provider.alpha", true),
            (Probe1Assembly, "OpWeave.FingerprintProbes.ProbeProviderExtra", "probe.type.extra", "probe.provider.extra", true),
            (Probe2Assembly, "OpWeave.FingerprintProbes.ProbeProviderGamma", "probe.type.gamma", "probe.provider.gamma", false),
            (EditorFixtureAssembly, "OpWeave.Fixtures.EditorFixtureMarker", "fixture.type.editor", "fixture.provider.editor", false),
            (PluginFixtureAssembly, "OpWeave.Fixtures.OpWeave_PluginMarker", "fixture.type.plugin", "fixture.provider.plugin", false),
            ("OpWeave.UnityAdapter", "OpWeave.UnityAdapter.WeakFingerprintProvider", "std.type.weakprovider", "std.provider.weakprovider", false),
            ("OpWeave.UnityAdapter.Tests", "OpWeave.UnityAdapter.Tests.WeakFingerprintTests", "std.type.weaktests", "std.provider.weaktests", false),
        };

        // The fingerprint pass of a Registry rebuild for the candidate set:
        // one line per candidate — opId|descriptorHash|implementationHash|
        // coreApiVersion — in candidate order.
        private static List<string> SnapshotFingerprints()
        {
            var lines = new List<string>();
            foreach (var (assembly, type, opId, _) in s_candidates)
            {
                var result = FingerprintOperation(opId, 1, assembly, type);
                Assert.That(result.Ok, Is.True,
                    () => $"fingerprint failed for {opId}: " +
                        string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
                var f = result.Value!;
                lines.Add($"{opId}|{f.DescriptorHash}|{f.ImplementationHash}|{f.CoreApiVersion}");
            }
            return lines;
        }

        private static Result<TypeFingerprint> FingerprintType(
            string typeId, string providerId, string assemblyName, string providerTypeName)
        {
            var typeDefinition = MustParse($"{{\"typeId\":\"{typeId}\",\"kind\":\"probe\"}}");
            return WeakFingerprintProvider.ForType(
                typeId, providerId, typeDefinition,
                LoadType(assemblyName, providerTypeName), CoreApiVersionForTests);
        }

        // The Type side of the same fingerprint pass: one line per type
        // candidate — typeId|providerId|typeDefinitionHash|
        // definingAssemblySha256|coreApiVersion — in candidate order.
        private static List<string> SnapshotTypeFingerprints()
        {
            var lines = new List<string>();
            foreach (var (assembly, providerType, typeId, providerId, _) in s_typeCandidates)
            {
                var result = FingerprintType(typeId, providerId, assembly, providerType);
                Assert.That(result.Ok, Is.True,
                    () => $"type fingerprint failed for {typeId}: " +
                        string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
                var f = result.Value!;
                lines.Add($"{typeId}|{providerId}|{f.TypeDefinitionHash}|" +
                    $"{f.Assembly.DefiningAssemblySha256}|{f.CoreApiVersion}");
            }
            return lines;
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            if (CleanupProbeArtifacts())
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (CleanupProbeArtifacts())
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTest]
        public IEnumerator MethodBodyEdit_ChangesOnlyThatAssemblysFingerprints()
        {
            // Domain 1: create the two probe assemblies and force a real
            // compile + domain reload.
            WriteProbeAssets(secondVariant: false);
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive, "the probe compile did not produce a domain reload");

            // Domain 2: probe types exist now. The edited body must really be
            // the compiled one ("alpha-v1"), then snapshot the fingerprint
            // pass — the BEFORE state of a Registry rebuild.
            Assert.That(
                LoadType(Probe1Assembly, "OpWeave.FingerprintProbes.ProbeOpAlpha")
                    .GetMethod("Body")!.Invoke(null, null),
                Is.EqualTo("alpha-v1"), "probe v1 body was not compiled");
            Directory.CreateDirectory(StateDir);
            File.WriteAllLines(BeforeSnapshotFile, SnapshotFingerprints());
            File.WriteAllLines(BeforeTypeSnapshotFile, SnapshotTypeFingerprints());

            // Edit only the method bodies of ProbeOps.cs (Probe1) and cross a
            // second real domain reload.
            WriteProbeOpsFile(
                Path.Combine(ProjectRoot, "Assets", "OpWeaveFingerprintProbe", "Probe1", "ProbeOps.cs"),
                secondVariant: true);
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive, "the method-body edit did not produce a domain reload");

            // Domain 3: prove the new body executes, rebuild the fingerprint
            // snapshot and compare per candidate.
            Assert.That(
                LoadType(Probe1Assembly, "OpWeave.FingerprintProbes.ProbeOpAlpha")
                    .GetMethod("Body")!.Invoke(null, null),
                Is.EqualTo("alpha-v2"), "the edited method body was not compiled");

            var before = File.ReadAllLines(BeforeSnapshotFile);
            var after = SnapshotFingerprints();
            Assert.That(after.Count, Is.EqualTo(before.Length));

            var changedOps = new List<string>();
            for (var i = 0; i < before.Length; i++)
            {
                var beforeParts = before[i].Split('|');
                var afterParts = after[i].Split('|');
                Assert.That(afterParts[0], Is.EqualTo(beforeParts[0]),
                    "candidate order drifted between snapshots");
                // Descriptor and Core API version are unchanged for every
                // candidate; only the implementation hash may move.
                Assert.That(afterParts[1], Is.EqualTo(beforeParts[1]),
                    $"descriptorHash moved for {beforeParts[0]}");
                Assert.That(afterParts[3], Is.EqualTo(beforeParts[3]),
                    $"coreApiVersion moved for {beforeParts[0]}");
                if (!string.Equals(afterParts[2], beforeParts[2], StringComparison.Ordinal))
                {
                    changedOps.Add(beforeParts[0]);
                }
            }

            var expectedChanged = s_candidates.Where(c => c.ChangedByEdit).Select(c => c.OpId).ToList();
            Assert.That(changedOps, Is.EquivalentTo(expectedChanged),
                "implementationHash must change only for the edited assembly's operations");

            // The Type fingerprints of the same rebuild must move exactly
            // with the provider's defining assembly: edited Probe1 providers
            // change implementationHash, Probe2 and the reference assemblies
            // stay byte-identical, and typeDefinitionHash never moves.
            var beforeTypes = File.ReadAllLines(BeforeTypeSnapshotFile);
            var afterTypes = SnapshotTypeFingerprints();
            Assert.That(afterTypes.Count, Is.EqualTo(beforeTypes.Length));

            var changedTypes = new List<string>();
            for (var i = 0; i < beforeTypes.Length; i++)
            {
                var beforeParts = beforeTypes[i].Split('|');
                var afterParts = afterTypes[i].Split('|');
                Assert.That(afterParts[0], Is.EqualTo(beforeParts[0]),
                    "type candidate order drifted between snapshots");
                Assert.That(afterParts[1], Is.EqualTo(beforeParts[1]),
                    $"providerId moved for {beforeParts[0]}");
                Assert.That(afterParts[2], Is.EqualTo(beforeParts[2]),
                    $"typeDefinitionHash moved for {beforeParts[0]}");
                Assert.That(afterParts[4], Is.EqualTo(beforeParts[4]),
                    $"coreApiVersion moved for {beforeParts[0]}");
                if (!string.Equals(afterParts[3], beforeParts[3], StringComparison.Ordinal))
                {
                    changedTypes.Add(beforeParts[0]);
                }
            }

            var expectedChangedTypes = s_typeCandidates
                .Where(c => c.ChangedByEdit).Select(c => c.TypeId).ToList();
            Assert.That(changedTypes, Is.EquivalentTo(expectedChangedTypes),
                "implementationHash must change only for the edited assembly's type providers");
        }
    }
}
