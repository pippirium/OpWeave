#nullable enable

// T-W2.2-08 [Edit] tests (SPEC §C2 test 2, §C3 tests 1–2): the structural
// attribute-vs-Descriptor consistency of every discovered Operation, the
// pre-instantiation OW-OP-004 refusal of a duplicated declared (id, version),
// the coexistence of the same id at different versions, and the OW-OP-007
// refusal of a reserved-namespace declared ID in a project assembly and in
// a UPM package — each refusal decided from the attribute alone, so the
// constructor-call counters stay at 0 (SPEC §C3 rules 3–5).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using UnityEditor;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.2-08:operation-identity — declarative Operation identity
    /// contract tests (SPEC §C2 item 2, §C3 stages 2–3 and rules 3–5).
    /// </summary>
    internal sealed class OperationIdentityTests
    {
        private const string ReservedProjectOperationId = "core.fixture.reservedns.op";
        private const string ReservedUpmOperationId = "unity.fixture.reservedns.op";
        private const string ReservedProjectType =
            "OpWeave.Fixtures.ReservedNamespace.ReservedNamespaceOperation";
        private const string ReservedUpmType =
            "com.opweave.fixture.reservedns.ReservedNsOperation";

        // The same seam shape RegistryPublicationTests uses: every delegate
        // is the production one except the candidate type list.
        private static RegistryTypeStage.Seams SeamsFor(
            IEnumerable<Type> operationTypes) =>
            new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => operationTypes,
                ProviderTypes: () => TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: type => EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());

        private static RegistryBuildResult Rebuild(
            RegistryTypeStage.Seams typeSeams, RegistryPublication publication) =>
            RegistryBuildHost.Rebuild(
                typeSeams, RegistryOperationStage.Production(), publication);

        private static RegistryBuildResult RebuildProduction()
        {
            var publication = new RegistryPublication();
            return RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                publication);
        }

        // Reads the static s_ctorCalls probe of a fixture class discovered
        // through TypeCache — the fixture classes live in other assemblies,
        // so they are reached by reflection like the §X.3.2 gate tests do.
        private static FieldInfo CtorProbeOf(string typeFullName)
        {
            var type = TypeCache
                .GetTypesWithAttribute<OpWeaveOperationAttribute>()
                .FirstOrDefault(t => t.FullName == typeFullName);
            Assert.That(type, Is.Not.Null, $"fixture type '{typeFullName}' was not discovered");
            var field = type!.GetField("s_ctorCalls", BindingFlags.Public | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, $"fixture type '{typeFullName}' lost its constructor probe");
            return field!;
        }

        // ── SPEC §C2 test 2: every discovered Operation's attribute declares
        //    the identity its Descriptor reports. ──
        [Test]
        public void EveryDiscoveredOperation_AttributeMatchesDescriptor()
        {
            var checkedCount = 0;
            var checkedStandard = false;
            var mismatches = new List<string>();
            foreach (var type in TypeCache.GetTypesWithAttribute<OpWeaveOperationAttribute>())
            {
                var declared = OperationEligibility.DeclaredIdentityOf(type);
                Assert.That(declared, Is.Not.Null,
                    type.FullName + ": the discovery attribute must carry (Id, Version)");
                Assert.That(declared!.Id, Is.Not.Null.And.Not.Empty,
                    type.FullName + ": the declared Id must not be empty");

                // A class violating the §C2 item-1 conditions or failing
                // construction produces no Descriptor to compare — its
                // OW-OP-008 is the eligibility tests' contract, not this
                // one's.
                if (OperationEligibility.Check(type).Count != 0)
                {
                    continue;
                }
                var instance = OperationEligibility.Instantiate(type);
                if (!instance.Ok || instance.Value is null)
                {
                    continue;
                }
                OperationDescriptor? descriptor;
                try
                {
                    descriptor = instance.Value!.Descriptor;
                }
                catch (Exception)
                {
                    continue;
                }
                if (descriptor is null)
                {
                    continue;
                }

                checkedCount++;
                if (string.Equals(type.Assembly.GetName().Name, "OpWeave.Operations", StringComparison.Ordinal))
                {
                    checkedStandard = true;
                }
                if (!string.Equals(declared.Id, descriptor.Id, StringComparison.Ordinal) ||
                    declared.Version != descriptor.Version)
                {
                    mismatches.Add(
                        $"{type.FullName}: attribute ({declared.Id}, {declared.Version}) vs " +
                        $"Descriptor ({descriptor.Id}, {descriptor.Version})");
                }
            }

            Assert.That(mismatches, Is.Empty,
                () => "every [OpWeaveOperation(id, version)] must equal its Descriptor " +
                    "identity (SPEC §C2 item 2):\n" + string.Join("\n", mismatches));
            Assert.That(checkedStandard, Is.True,
                "the structural check must cover the standard Operations");
            Assert.That(checkedCount, Is.GreaterThan(30),
                "the standard Operations plus the project and fixture Ops were all checked");
        }

        // ── SPEC §C3 test 2: two classes declaring the same (id, version)
        //    are both refused OW-OP-004 — decided on the attribute before
        //    either constructor runs. ──
        [Test]
        public void DuplicateDeclaredIdentity_RefusedBeforeConstruction()
        {
            OperationIdentityFixtures.DuplicateIdentityOperationA.s_ctorCalls = 0;
            OperationIdentityFixtures.DuplicateIdentityOperationB.s_ctorCalls = 0;

            var publication = new RegistryPublication();
            var result = Rebuild(SeamsFor(new[]
            {
                typeof(OperationIdentityFixtures.DuplicateIdentityOperationA),
                typeof(OperationIdentityFixtures.DuplicateIdentityOperationB),
                typeof(OperationIdentityFixtures.UnrelatedIdentityOperation),
            }), publication);

            Assert.That(result.Stale, Is.False);
            var snapshot = result.Snapshot!;

            Assert.That(
                OperationIdentityFixtures.DuplicateIdentityOperationA.s_ctorCalls,
                Is.EqualTo(0),
                "a declared-identity duplicate must never be constructed (SPEC §C3 rule 4)");
            Assert.That(
                OperationIdentityFixtures.DuplicateIdentityOperationB.s_ctorCalls,
                Is.EqualTo(0),
                "a declared-identity duplicate must never be constructed (SPEC §C3 rule 4)");

            // Both candidates are refused with OW-OP-004; the unrelated
            // Operation registers (SPEC §C3 step 6).
            var duplicates = snapshot.Failures
                .Where(f => f.OperationId == OperationIdentityFixtures.DuplicateOperationId)
                .ToList();
            Assert.That(duplicates, Has.Count.EqualTo(2));
            Assert.That(duplicates.All(f => f.Kind == RegistryChangeKinds.Failed), Is.True);
            Assert.That(
                duplicates.All(f => f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_004)),
                Is.True);
            Assert.That(
                snapshot.Find(OperationIdentityFixtures.DuplicateOperationId, 1), Is.Null);
            Assert.That(
                snapshot.Find(OperationIdentityFixtures.UnrelatedOperationId, 1), Is.Not.Null,
                "an unrelated Operation must still register");
        }

        // ── SPEC §C3 rule 4: the same declared id at a different version
        //    coexists — no refusal. ──
        [Test]
        public void SameDeclaredIdDifferentVersions_BothRegister()
        {
            var publication = new RegistryPublication();
            var result = Rebuild(SeamsFor(new[]
            {
                typeof(OperationIdentityFixtures.CoexistingVersionOneOperation),
                typeof(OperationIdentityFixtures.CoexistingVersionTwoOperation),
            }), publication);

            var snapshot = result.Snapshot!;
            Assert.That(
                snapshot.Find(OperationIdentityFixtures.CoexistingOperationId, 1), Is.Not.Null);
            Assert.That(
                snapshot.Find(OperationIdentityFixtures.CoexistingOperationId, 2), Is.Not.Null);
            Assert.That(
                snapshot.Failures.Any(f =>
                    f.OperationId == OperationIdentityFixtures.CoexistingOperationId),
                Is.False);
        }

        // ── SPEC §C3 test 1 (reserved-namespace part): a Custom Operation
        //    declaring a reserved ID is refused OW-OP-007 on the attribute —
        //    the constructor never runs and nothing registers — once for a
        //    project assembly and once for a UPM package. ──
        [Test]
        public void ReservedDeclaredId_ProjectAssembly_RefusedBeforeConstruction()
        {
            var probe = CtorProbeOf(ReservedProjectType);
            probe.SetValue(null, 0);

            var result = RebuildProduction();

            Assert.That(probe.GetValue(null), Is.EqualTo(0),
                "a reserved-namespace declared ID must never be constructed (SPEC §C3 rule 3)");
            var snapshot = result.Snapshot!;
            Assert.That(snapshot.Find(ReservedProjectOperationId, 1), Is.Null);
            Assert.That(snapshot.Failures.Any(f =>
                    f.OperationId == ReservedProjectOperationId &&
                    f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_007)),
                Is.True,
                "the project-assembly reserved ID must surface as OW-OP-007 in the snapshot");
        }

        [Test]
        public void ReservedDeclaredId_UpmPackage_RefusedBeforeConstruction()
        {
            var probe = CtorProbeOf(ReservedUpmType);
            probe.SetValue(null, 0);

            var result = RebuildProduction();

            Assert.That(probe.GetValue(null), Is.EqualTo(0),
                "a reserved-namespace declared ID must never be constructed (SPEC §C3 rule 3)");
            var snapshot = result.Snapshot!;
            Assert.That(snapshot.Find(ReservedUpmOperationId, 1), Is.Null);
            Assert.That(snapshot.Failures.Any(f =>
                    f.OperationId == ReservedUpmOperationId &&
                    f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_007)),
                Is.True,
                "the UPM-package reserved ID must surface as OW-OP-007 in the snapshot");
        }
    }
}
