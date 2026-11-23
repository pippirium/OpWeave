#nullable enable
// T-W2.12-01 [Edit] tests (live discovery half): local:T-W2.12-01:extension-metadata —
// real UPM fixture packages committed under TestProject/Packages/ (embedded
// packages are scanned at project load; runtime-written package dirs are not,
// which is why these fixtures are committed rather than installed mid-test —
// no manifest.json/packages-lock.json change). The valid fixture registers;
// every invalid variant is rejected entirely; coreApi failures (out of
// range, min ≥ max, malformed bound, missing bound, missing object) return
// OW-OP-012 with the declared range and the current CoreApi.Version in the
// hint; an unparseable manifest, an unsupported schemaVersion, a malformed
// extensionId, a malformed or above-Editor unity.minInclusive, a declared
// assembly not rooted in the package, a provider class in an undeclared
// package assembly, a typeProviders assembly reference outside
// 'assemblies', a declared Provider ID with no [OpWeaveTypeProvider] class
// in the named assembly and a provider class/ID count mismatch, example
// paths escaping or missing inside the package, reserved
// extensionId/Provider ID/Operation ID/Type ID, in-manifest duplicate
// Provider IDs and Operation (id, version) pairs, duplicate Provider IDs,
// Operation (id, version) identities and offered Type IDs across packages,
// declared-vs-observed operation-list mismatch and non-empty blueprints
// are each rejected as a whole extension (blueprints with OW-OP-010).
// Observed Operation Descriptor identities reach discovery through the
// Discover(observedOperations) entry point, attributed to packages by
// assembly name — the reservedop, dupop and dupop1/dupop2 fixtures each
// declare operations equal to their observed Descriptor set so the named
// rule is the fixture's only violation. Offered Type IDs reach discovery
// through Discover(observedTypes): the test instantiates each fixture
// package's [OpWeaveTypeProvider] class the way the registration stage
// will (SPEC §C3 steps 3–4) and reports the Type IDs GetTypes() actually
// yields — dupty1/dupty2 really offer one shared Type ID and reservedtype
// really offers a core.* Type ID. The valid fixture's declared
// example is a complete workflow that passes the static Validator
// (SPEC §X.7). The [UnityTest] re-runs discovery across a real domain
// reload (SPEC §T.3 pattern: RecompileScripts + domain sentinel).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.12-01:extension-metadata — embedded fixture packages with
    /// valid and invalid opweave.extension.json variants discovered and
    /// validated in the real project.
    /// </summary>
    internal sealed class ExtensionDiscoveryTests
    {
        private const string ValidPackage = "com.opweave.fixture.valid";
        private const string BadSchemaPackage = "com.opweave.fixture.badschema";
        private const string InvRangePackage = "com.opweave.fixture.invrange";
        private const string UnityMinPackage = "com.opweave.fixture.unitymin";
        private const string UnityBadPackage = "com.opweave.fixture.unitybad";
        private const string BadAsmPackage = "com.opweave.fixture.badasm";
        private const string BadExamplesPackage = "com.opweave.fixture.badexamples";
        private const string BadRangePackage = "com.opweave.fixture.badrange";
        private const string BadBoundPackage = "com.opweave.fixture.badbound";
        private const string NoBoundPackage = "com.opweave.fixture.nobound";
        private const string NoCoreApiPackage = "com.opweave.fixture.nocoreapi";
        private const string BlueprintsPackage = "com.opweave.fixture.blueprints";
        private const string NoManifestPackage = "com.opweave.fixture.nomanifest";
        private const string MismatchPackage = "com.opweave.fixture.mismatch";
        private const string Dup1Package = "com.opweave.fixture.dup1";
        private const string Dup2Package = "com.opweave.fixture.dup2";
        private const string ReservedExtPackage = "com.opweave.fixture.reservedext";
        private const string ReservedPidPackage = "com.opweave.fixture.reservedpid";
        private const string ReservedOpPackage = "com.opweave.fixture.reservedop";
        private const string DupProvPackage = "com.opweave.fixture.dupprov";
        private const string DupProv1Package = "com.opweave.fixture.dupprov1";
        private const string DupProv2Package = "com.opweave.fixture.dupprov2";
        private const string DupOpPackage = "com.opweave.fixture.dupop";
        private const string DupOp1Package = "com.opweave.fixture.dupop1";
        private const string DupOp2Package = "com.opweave.fixture.dupop2";
        private const string BadJsonPackage = "com.opweave.fixture.badjson";
        private const string BadExtIdPackage = "com.opweave.fixture.badextid";
        private const string NoProviderPackage = "com.opweave.fixture.noprovider";
        private const string UndeclaredAsmPackage = "com.opweave.fixture.undeclaredasm";
        private const string BadProvRefPackage = "com.opweave.fixture.badprovref";
        private const string ExtraProvPackage = "com.opweave.fixture.extraprov";
        private const string DupTy1Package = "com.opweave.fixture.dupty1";
        private const string DupTy2Package = "com.opweave.fixture.dupty2";
        private const string ReservedTypePackage = "com.opweave.fixture.reservedtype";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // A transient script written into the valid fixture package to force
        // recompilation — the reload mechanism for the domain-crossing test.
        private static string ReloadMarkerPath =>
            Path.Combine(ProjectRoot, "Packages", ValidPackage, "Editor", "ReloadProbeMarker.cs");

        private const string ReloadMarkerSource =
            "#nullable enable\n" +
            "namespace com.opweave.fixture.valid\n{\n" +
            "    internal static class ReloadProbeMarker\n    {\n" +
            "        public const int Value = 1;\n    }\n" +
            "}\n";

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var dirty = false;
            if (File.Exists(ReloadMarkerPath))
            {
                File.Delete(ReloadMarkerPath);
                dirty = true;
            }
            var meta = ReloadMarkerPath + ".meta";
            if (File.Exists(meta))
            {
                File.Delete(meta);
                dirty = true;
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
                yield return new RecompileScripts(false);
            }
        }

        private static ExtensionValidationOutcome? OutcomeFor(
            ExtensionDiscoveryReport report, string packageId) =>
            report.Extensions.FirstOrDefault(e => e.PackageId == packageId);

        // Observed Operation Descriptor identities supplied the way the
        // registration stage will once [OpWeaveOperation] exists
        // (T-W1.5-03): each entry attributes an (id, version) observation
        // to a fixture package through its declared Editor assembly.
        // reservedop and dupop then declare operations equal to what their
        // packages contain — the reserved prefix / in-manifest duplicate
        // stays the single violation — and dupop1/dupop2 carry matching
        // observed identities so only the cross-package duplicate rejects
        // them.
        private static IReadOnlyList<ObservedExtensionOperation> FixtureObservedOperations() =>
            new[]
            {
                new ObservedExtensionOperation(
                    "com.opweave.fixture.valid.op", 1, "OpWeave.Fixture.Valid.Editor"),
                new ObservedExtensionOperation(
                    "opweave.fixture.reserved.op", 1, "OpWeave.Fixture.ReservedOp.Editor"),
                new ObservedExtensionOperation(
                    "com.opweave.fixture.dupop.op", 1, "OpWeave.Fixture.DupOp.Editor"),
                new ObservedExtensionOperation(
                    "com.opweave.fixture.dupopshared.op", 1, "OpWeave.Fixture.DupOp1.Editor"),
                new ObservedExtensionOperation(
                    "com.opweave.fixture.dupopshared.op", 1, "OpWeave.Fixture.DupOp2.Editor"),
            };

        // Offered Type IDs the way the registration stage will supply them
        // (SPEC §C3 steps 3–4): the fixture providers are real
        // [OpWeaveTypeProvider] classes compiled into their packages' Editor
        // assemblies, so the test instantiates each packaged provider and
        // reports the Type IDs its GetTypes() actually yields, attributed by
        // defining assembly. dupty1/dupty2 both really offer
        // com.opweave.fixture.duptyshared.type and reservedtype really
        // offers a core.* Type ID; every other fixture provider yields an
        // empty set.
        private static IReadOnlyList<ObservedExtensionType> FixtureObservedTypes()
        {
            var observed = new List<ObservedExtensionType>();
            foreach (var type in TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>())
            {
                var provenance = EditorAssemblyResolver.ResolveForType(type);
                if (!provenance.Ok || provenance.Value!.PackageId is null ||
                    string.Equals(provenance.Value.PackageId,
                        EditorAssemblyResolver.StandardPackageId, StringComparison.Ordinal))
                {
                    continue;
                }
                var assemblyName = provenance.Value.AssemblyName;
                try
                {
                    if (Activator.CreateInstance(type) is ITypeProvider provider &&
                        provider.GetTypes() is { } definitions)
                    {
                        foreach (var definition in definitions)
                        {
                            if (definition is not null)
                            {
                                observed.Add(new ObservedExtensionType(definition.Id, assemblyName));
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // A provider that cannot be instantiated or enumerated
                    // yields no observations — the package's other rules
                    // still decide its outcome.
                }
            }
            return observed;
        }

        private static void AssertFixtureOutcomes(ExtensionDiscoveryReport report)
        {
            // Valid registers: accepted, manifest parsed, provider bound.
            var valid = OutcomeFor(report, ValidPackage);
            Assert.That(valid, Is.Not.Null, "valid fixture package was not discovered");
            Assert.That(valid!.Accepted, Is.True,
                () => string.Join("; ", valid.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(valid.Manifest!.ExtensionId, Is.EqualTo("com.opweave.fixture.valid"));
            Assert.That(valid.TypeProviders, Has.Count.EqualTo(1));
            Assert.That(valid.TypeProviders[0].ProviderId,
                Is.EqualTo("com.opweave.fixture.valid.types"));
            Assert.That(valid.TypeProviders[0].AssemblyName,
                Is.EqualTo("OpWeave.Fixture.Valid.Editor"));
            Assert.That(valid.TypeProviders[0].TypeFullName,
                Is.EqualTo(ValidPackage + ".ValidProvider"));

            // Manifest present but not parseable JSON → rejected entirely
            // (SG-02 internal diagnostics; the published OW-OP-013..019
            // mapping is T-W2.12-03). The package ships a real
            // [OpWeaveTypeProvider] class so the rejection cannot be
            // vacuous: it must never appear in any accepted outcome's
            // provider bindings.
            var badJson = OutcomeFor(report, BadJsonPackage);
            Assert.That(badJson, Is.Not.Null);
            Assert.That(badJson!.Accepted, Is.False);
            Assert.That(badJson.Diagnostics, Is.Not.Empty);
            Assert.That(badJson.TypeProviders, Is.Empty);
            Assert.That(report.Extensions
                .Where(e => e.Accepted)
                .SelectMany(e => e.TypeProviders)
                .Select(b => b.TypeFullName),
                Has.None.EqualTo("com.opweave.fixture.badjson.BadJsonProvider"),
                "a provider from an unparseable-manifest package must never be bound");

            // Unsupported schemaVersion → rejected entirely (internal
            // diagnostic; the published OW-OP-013..019 mapping is
            // T-W2.12-03).
            var badSchema = OutcomeFor(report, BadSchemaPackage);
            Assert.That(badSchema, Is.Not.Null);
            Assert.That(badSchema!.Accepted, Is.False);
            Assert.That(badSchema.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("schemaVersion").And.Some.Contains("not supported"));
            Assert.That(badSchema.TypeProviders, Is.Empty);

            // Malformed extensionId → rejected entirely (internal
            // diagnostic; reserved prefixes are covered separately below).
            var badExtId = OutcomeFor(report, BadExtIdPackage);
            Assert.That(badExtId, Is.Not.Null);
            Assert.That(badExtId!.Accepted, Is.False);
            Assert.That(badExtId.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("extensionId").And.Some.Contains("malformed"));
            Assert.That(badExtId.TypeProviders, Is.Empty);

            // coreApi out of range → OW-OP-012; nothing registers.
            var badRange = OutcomeFor(report, BadRangePackage);
            Assert.That(badRange, Is.Not.Null);
            Assert.That(badRange!.Accepted, Is.False);
            Assert.That(badRange.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(badRange.Diagnostics[0].Hint,
                Does.Contain("0.9.0").And.Contain("0.9.9").And.Contain(CoreApi.Version));
            Assert.That(badRange.TypeProviders, Is.Empty);

            // coreApi minInclusive ≥ maxExclusive → OW-OP-012 with declared
            // range + current version.
            var invRange = OutcomeFor(report, InvRangePackage);
            Assert.That(invRange, Is.Not.Null);
            Assert.That(invRange!.Accepted, Is.False);
            Assert.That(invRange.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(invRange.Diagnostics[0].Hint,
                Does.Contain("'2.0.0'..'1.0.0'").And.Contain(CoreApi.Version));
            Assert.That(invRange.TypeProviders, Is.Empty);

            // coreApi malformed bound → OW-OP-012 with declared range + current version.
            var badBound = OutcomeFor(report, BadBoundPackage);
            Assert.That(badBound, Is.Not.Null);
            Assert.That(badBound!.Accepted, Is.False);
            Assert.That(badBound.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(badBound.Diagnostics[0].Hint,
                Does.Contain("'1.0'..'2.0.0'").And.Contain(CoreApi.Version));
            Assert.That(badBound.TypeProviders, Is.Empty);

            // coreApi missing bound → OW-OP-012.
            var noBound = OutcomeFor(report, NoBoundPackage);
            Assert.That(noBound, Is.Not.Null);
            Assert.That(noBound!.Accepted, Is.False);
            Assert.That(noBound.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(noBound.Diagnostics[0].Hint,
                Does.Contain("'1.0.0'..'<missing>'").And.Contain(CoreApi.Version));
            Assert.That(noBound.TypeProviders, Is.Empty);

            // coreApi object absent → OW-OP-012.
            var noCoreApi = OutcomeFor(report, NoCoreApiPackage);
            Assert.That(noCoreApi, Is.Not.Null);
            Assert.That(noCoreApi!.Accepted, Is.False);
            Assert.That(noCoreApi.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_012));
            Assert.That(noCoreApi.Diagnostics[0].Hint,
                Does.Contain(CoreApi.Version));
            Assert.That(noCoreApi.TypeProviders, Is.Empty);

            // unity.minInclusive above the running Editor → rejected entirely.
            var unityMin = OutcomeFor(report, UnityMinPackage);
            Assert.That(unityMin, Is.Not.Null);
            Assert.That(unityMin!.Accepted, Is.False);
            Assert.That(unityMin.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("9999.0"));
            Assert.That(unityMin.TypeProviders, Is.Empty);

            // Malformed unity.minInclusive → rejected entirely.
            var unityBad = OutcomeFor(report, UnityBadPackage);
            Assert.That(unityBad, Is.Not.Null);
            Assert.That(unityBad!.Accepted, Is.False);
            Assert.That(unityBad.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("unity.minInclusive").And.Some.Contains("malformed"));
            Assert.That(unityBad.TypeProviders, Is.Empty);

            // Declared assembly not rooted in the package → rejected.
            var badAsm = OutcomeFor(report, BadAsmPackage);
            Assert.That(badAsm, Is.Not.Null);
            Assert.That(badAsm!.Accepted, Is.False);
            Assert.That(badAsm.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("OpWeave.Fixture.BadAsm.Ghost.Editor")
                    .And.Some.Contains("not rooted"));
            Assert.That(badAsm.TypeProviders, Is.Empty);

            // Provider class in a package assembly the manifest does not
            // declare → rejected entirely.
            var undeclaredAsm = OutcomeFor(report, UndeclaredAsmPackage);
            Assert.That(undeclaredAsm, Is.Not.Null);
            Assert.That(undeclaredAsm!.Accepted, Is.False);
            Assert.That(undeclaredAsm.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("undeclared assembly")
                    .And.Some.Contains("OpWeave.Fixture.UndeclaredAsm.Editor"));
            Assert.That(undeclaredAsm.TypeProviders, Is.Empty);

            // typeProviders entry naming an assembly not listed in
            // 'assemblies' → rejected entirely.
            var badProvRef = OutcomeFor(report, BadProvRefPackage);
            Assert.That(badProvRef, Is.Not.Null);
            Assert.That(badProvRef!.Accepted, Is.False);
            Assert.That(badProvRef.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("OpWeave.Fixture.BadProvRef.Other.Editor")
                    .And.Some.Contains("not listed in 'assemblies'"));
            Assert.That(badProvRef.TypeProviders, Is.Empty);

            // Declared Provider ID but no [OpWeaveTypeProvider] class in the
            // named (existing) package assembly → rejected entirely.
            var noProvider = OutcomeFor(report, NoProviderPackage);
            Assert.That(noProvider, Is.Not.Null);
            Assert.That(noProvider!.Accepted, Is.False);
            Assert.That(noProvider.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("No [OpWeaveTypeProvider] class found")
                    .And.Some.Contains("OpWeave.Fixture.NoProvider.Editor"));
            Assert.That(noProvider.TypeProviders, Is.Empty);

            // Two provider classes but one declared Provider ID → the
            // one-to-one pairing fails and the extension is rejected.
            var extraProv = OutcomeFor(report, ExtraProvPackage);
            Assert.That(extraProv, Is.Not.Null);
            Assert.That(extraProv!.Accepted, Is.False);
            Assert.That(extraProv.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("2 [OpWeaveTypeProvider] class(es)")
                    .And.Some.Contains("1 Provider ID(s)"));
            Assert.That(extraProv.TypeProviders, Is.Empty);

            // Example paths escaping the package root or missing inside it
            // → rejected entirely.
            var badExamples = OutcomeFor(report, BadExamplesPackage);
            Assert.That(badExamples, Is.Not.Null);
            Assert.That(badExamples!.Accepted, Is.False);
            Assert.That(badExamples.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("escapes the package root")
                    .And.Some.Contains("was not found"));
            Assert.That(badExamples.TypeProviders, Is.Empty);

            // Non-empty blueprints → OW-OP-010.
            var blueprints = OutcomeFor(report, BlueprintsPackage);
            Assert.That(blueprints, Is.Not.Null);
            Assert.That(blueprints!.Accepted, Is.False);
            Assert.That(blueprints.Diagnostics.Select(d => d.Code),
                Has.All.EqualTo(ErrorCodes.OW_OP_010));
            Assert.That(blueprints.TypeProviders, Is.Empty);

            // Candidates without a manifest → rejected entirely.
            var noManifest = OutcomeFor(report, NoManifestPackage);
            Assert.That(noManifest, Is.Not.Null);
            Assert.That(noManifest!.Accepted, Is.False);
            Assert.That(noManifest.ManifestPresent, Is.False);
            Assert.That(noManifest.TypeProviders, Is.Empty);

            // Declared operations ≠ discovered Descriptors → rejected.
            var mismatch = OutcomeFor(report, MismatchPackage);
            Assert.That(mismatch, Is.Not.Null);
            Assert.That(mismatch!.Accepted, Is.False);
            Assert.That(mismatch.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("does not match"));
            Assert.That(mismatch.TypeProviders, Is.Empty);

            // Duplicate extensionId across packages → both rejected.
            var dup1 = OutcomeFor(report, Dup1Package);
            var dup2 = OutcomeFor(report, Dup2Package);
            Assert.That(dup1, Is.Not.Null);
            Assert.That(dup2, Is.Not.Null);
            Assert.That(dup1!.Accepted, Is.False);
            Assert.That(dup2!.Accepted, Is.False);
            Assert.That(dup1.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("more than one package"));

            // Reserved namespaces: extensionId, Provider ID, Operation ID
            // and offered Type ID each reject the whole extension (internal
            // diagnostic; the published OW-OP-013..019 mapping is
            // T-W2.12-03).
            var reservedExt = OutcomeFor(report, ReservedExtPackage);
            Assert.That(reservedExt, Is.Not.Null);
            Assert.That(reservedExt!.Accepted, Is.False);
            Assert.That(reservedExt.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("reserved"));
            Assert.That(reservedExt.TypeProviders, Is.Empty);

            var reservedPid = OutcomeFor(report, ReservedPidPackage);
            Assert.That(reservedPid, Is.Not.Null);
            Assert.That(reservedPid!.Accepted, Is.False);
            Assert.That(reservedPid.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Provider ID").And.Some.Contains("reserved"));
            Assert.That(reservedPid.TypeProviders, Is.Empty);

            var reservedOp = OutcomeFor(report, ReservedOpPackage);
            Assert.That(reservedOp, Is.Not.Null);
            Assert.That(reservedOp!.Accepted, Is.False);
            Assert.That(reservedOp.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Operation ID").And.Some.Contains("reserved"));
            Assert.That(reservedOp.TypeProviders, Is.Empty);

            // Its provider really offers a core.* Type ID → rejected
            // entirely.
            var reservedType = OutcomeFor(report, ReservedTypePackage);
            Assert.That(reservedType, Is.Not.Null);
            Assert.That(reservedType!.Accepted, Is.False);
            Assert.That(reservedType.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("reserved"));
            Assert.That(reservedType.TypeProviders, Is.Empty);

            // In-manifest duplicate Provider ID / Operation (id, version).
            var dupProv = OutcomeFor(report, DupProvPackage);
            Assert.That(dupProv, Is.Not.Null);
            Assert.That(dupProv!.Accepted, Is.False);
            Assert.That(dupProv.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("duplicate Provider ID"));
            Assert.That(dupProv.TypeProviders, Is.Empty);

            var dupOp = OutcomeFor(report, DupOpPackage);
            Assert.That(dupOp, Is.Not.Null);
            Assert.That(dupOp!.Accepted, Is.False);
            Assert.That(dupOp.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("duplicate Operation"));
            Assert.That(dupOp.TypeProviders, Is.Empty);

            // Duplicate Provider ID across packages → both rejected.
            var dupProv1 = OutcomeFor(report, DupProv1Package);
            var dupProv2 = OutcomeFor(report, DupProv2Package);
            Assert.That(dupProv1, Is.Not.Null);
            Assert.That(dupProv2, Is.Not.Null);
            Assert.That(dupProv1!.Accepted, Is.False);
            Assert.That(dupProv2!.Accepted, Is.False);
            Assert.That(dupProv1.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Provider ID").And.Some.Contains("more than one package"));
            Assert.That(dupProv1.TypeProviders, Is.Empty);
            Assert.That(dupProv2.TypeProviders, Is.Empty);

            // Duplicate Operation (id, version) across packages → both
            // rejected, no provider bindings. Each fixture's declared
            // operations[] equals the observed Descriptor set supplied to
            // Discover(), so the cross-package duplicate is the only
            // violation.
            var dupOp1 = OutcomeFor(report, DupOp1Package);
            var dupOp2 = OutcomeFor(report, DupOp2Package);
            Assert.That(dupOp1, Is.Not.Null);
            Assert.That(dupOp2, Is.Not.Null);
            Assert.That(dupOp1!.Accepted, Is.False);
            Assert.That(dupOp2!.Accepted, Is.False);
            Assert.That(dupOp1.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Operation").And.Some.Contains("more than one package"));
            Assert.That(dupOp2.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Operation").And.Some.Contains("more than one package"));
            Assert.That(dupOp1.TypeProviders, Is.Empty);
            Assert.That(dupOp2.TypeProviders, Is.Empty);

            // Duplicate offered Type ID across packages → both rejected, no
            // type or provider bindings. Both providers really yield the
            // same Type ID, observed through instantiated GetTypes().
            var dupTy1 = OutcomeFor(report, DupTy1Package);
            var dupTy2 = OutcomeFor(report, DupTy2Package);
            Assert.That(dupTy1, Is.Not.Null);
            Assert.That(dupTy2, Is.Not.Null);
            Assert.That(dupTy1!.Accepted, Is.False);
            Assert.That(dupTy2!.Accepted, Is.False);
            Assert.That(dupTy1.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("more than one package"));
            Assert.That(dupTy2.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("Type ID").And.Some.Contains("more than one package"));
            Assert.That(dupTy1.TypeProviders, Is.Empty);
            Assert.That(dupTy2.TypeProviders, Is.Empty);
        }

        // ---------- local:T-W2.12-01:extension-metadata ----------

        [Test]
        public void InstalledFixturePackages_ValidRegisters_InvalidRejectedEntirely()
        {
            var report = ExtensionDiscovery.Discover(
                FixtureObservedOperations(), FixtureObservedTypes());

            Assert.That(report.Extensions.Select(e => e.PackageId),
                Has.None.EqualTo(EditorAssemblyResolver.StandardPackageId),
                "the standard package is never an extension candidate");
            AssertFixtureOutcomes(report);
        }

        // SPEC §T.3: the same discovery outcome must hold after a real
        // domain reload. A marker script is added to the valid fixture
        // package so the recompile is genuine. Locals do not survive the
        // reload, so every assertion re-derives its state post-reload.
        [UnityTest]
        public IEnumerator DiscoveryOutcome_SurvivesDomainReload()
        {
            File.WriteAllText(ReloadMarkerPath, ReloadMarkerSource);
            AssetDatabase.Refresh();
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the script change did not produce a domain reload");

            var report = ExtensionDiscovery.Discover(
                FixtureObservedOperations(), FixtureObservedTypes());
            AssertFixtureOutcomes(report);
        }

        // SPEC §X.7: a declared example must be a complete workflow that
        // passes the static Validator — never a node fragment. The valid
        // fixture's manifest lists this file in 'examples'.
        [Test]
        public void ValidFixtureExample_IsACompleteValidWorkflow()
        {
            var examplePath = Path.Combine(
                ProjectRoot, "Packages", ValidPackage,
                "Samples~", "Workflows", "example.opweave.json");
            Assert.That(File.Exists(examplePath), Is.True,
                "the declared example file is missing from the valid fixture package");

            var read = WorkflowSerializer.Read(File.ReadAllText(examplePath));
            Assert.That(read.Ok, Is.True,
                () => string.Join("; ", read.Diagnostics.Select(d => d.Code + " " + d.Message)));

            var report = new WorkflowCompiler().Validate(new ValidationRequest(
                read.Value!, null, ValidationMode.Static, ActorKind.Agent));
            Assert.That(report.Ok, Is.True,
                () => string.Join("; ", report.Errors.Select(d => d.Code + " " + d.Message)));
        }
    }
}
