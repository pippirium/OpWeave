#nullable enable
// T-W2.10-04 [Edit] tests: SPEC §0.5 structural test (AT-F06, X.11). No
// production assembly and no custom-Operation assembly may reference the
// test-support set — OpWeave.Testing, NUnit or Mono.Cecil. Reference
// information is read from each compiled assembly's own reference table
// (SPEC §H2 item 7 approach): under testables Unity injects the test
// framework into every testable package assembly's compiler inputs, so the
// produced DLL metadata is the source that separates a real violation from
// that injection. The deliberately violating fixture assembly
// OpWeaveFixtures.TestSupportReference lives in
// TestProject/Assets/OpWeaveFixtures/ and proves the check is not vacuous.
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class ProductionAssemblyIsolationTests
    {
        // SPEC §0.5: the test-support assembly and the two precompiled test
        // frameworks are confined to OpWeave.Testing and test assemblies.
        // T-W1.3-04 adds the JSON Schema validator chain under
        // Tests/Plugins/, which is likewise referenced only by test
        // assemblies (local:T-W1.3-04:schema-fixtures).
        private static readonly string[] ForbiddenReferenceNames =
        {
            "OpWeave.Testing",
            "nunit.framework",
            "Mono.Cecil",
            "JsonSchema.Net",
            "JsonPointer.Net",
            "Json.More",
            "Humanizer",
            "System.Text.Json",
            "System.Text.Encodings.Web",
            "Microsoft.Bcl.AsyncInterfaces",
            "System.IO.Pipelines",
            "System.Runtime.CompilerServices.Unsafe",
        };

        // Compiled on every supported Editor version; a missing entry would
        // make the scan silently empty, so presence is asserted.
        private static readonly string[] RequiredProductionAssemblies =
        {
            "OpWeave.Core",
            "OpWeave.UnityAdapter",
            "OpWeave.Operations",
            "OpWeave.AgentApi",
            "OpWeave.Viewer",
        };

        // Inspected whenever compiled; all may legitimately be absent:
        // OpWeave.Pipeline needs com.unity.pipeline (SPEC §H2 item 1),
        // OpWeave.GraphToolkit needs Unity 6000.5 (SPEC §0.5), OpWeave.Custom
        // is the user's custom-Operation assembly (SPEC §M.2) and does not
        // exist in TestProject.
        private static readonly string[] ConditionalAssemblies =
        {
            "OpWeave.Pipeline",
            "OpWeave.GraphToolkit",
            "OpWeave.Custom",
        };

        private const string ViolatingFixtureAssemblyName = "OpWeaveFixtures.TestSupportReference";

        [Test]
        public void ProductionAssemblies_HaveNoTestSupportReferences()
        {
            var inspected = new List<string>();
            foreach (var name in RequiredProductionAssemblies)
            {
                var assembly = AssemblyReferenceInspector.FindAssembly(name);
                Assert.That(assembly, Is.Not.Null, $"required production assembly {name} is not compiled");
                AssertNoForbiddenReferences(assembly!, name);
                inspected.Add(name);
            }

            foreach (var name in ConditionalAssemblies)
            {
                var assembly = AssemblyReferenceInspector.FindAssembly(name);
                if (assembly is null)
                {
                    continue;
                }
                AssertNoForbiddenReferences(assembly, name);
                inspected.Add(name);
            }
            Assert.That(inspected.Count, Is.GreaterThanOrEqualTo(RequiredProductionAssemblies.Length));
        }

        [Test]
        public void ViolatingFixture_NUnitReferenceIsDetected()
        {
            // The fixture is a production-style Editor assembly that
            // references nunit.framework; the same inspection must flag it.
            var fixture = AssemblyReferenceInspector.FindAssembly(ViolatingFixtureAssemblyName);
            Assert.That(fixture, Is.Not.Null, "violating fixture assembly is not compiled");
            Assert.That(ForbiddenReferences(fixture!), Does.Contain("nunit.framework"));
        }

        private static void AssertNoForbiddenReferences(Assembly assembly, string name)
        {
            var violations = ForbiddenReferences(assembly);
            Assert.That(violations, Is.Empty,
                $"{name} must not reference the test-support set: {string.Join(", ", violations)}");
        }

        private static List<string> ForbiddenReferences(Assembly assembly)
        {
            var violations = new List<string>();
            var forbidden = new HashSet<string>(ForbiddenReferenceNames, StringComparer.Ordinal);
            foreach (var reference in AssemblyReferenceInspector.ReferencedAssemblyNames(assembly))
            {
                if (forbidden.Contains(reference) && !violations.Contains(reference))
                {
                    violations.Add(reference);
                }
            }
            return violations;
        }
    }
}
