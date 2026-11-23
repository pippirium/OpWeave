#nullable enable
// T-W3.3-03 [Edit] tests: the SPEC §H2 item 7 structural test — no OpWeave
// assembly other than OpWeave.Pipeline, and no custom-Operation assembly,
// may reference Unity.Pipeline*. Reference information is read from each
// compiled assembly's own metadata reference table through the
// AssemblyReferenceInspector of T-W2.10-04 — the produced DLL metadata is
// the source that separates a real violation from references the Unity
// compilation model injects under testables. The deliberately violating
// fixture assembly OpWeaveFixtures.PipelineReference lives in
// TestProject/Assets/OpWeaveFixtures/ and proves the check is not vacuous.
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class PipelineReferenceIsolationTests
    {
        // SPEC §H2 item 7 prohibits every reference whose simple name starts
        // with "Unity.Pipeline" — Unity.Pipeline, Unity.Pipeline.Attributes
        // and any later assembly under that prefix.
        private const string ForbiddenPrefix = "Unity.Pipeline";
        private const string AdapterAssemblyName = "OpWeave.Pipeline";
        private const string ViolatingFixtureAssemblyName = "OpWeaveFixtures.PipelineReference";

        // Compiled whenever the EditMode test assemblies exist: the §0.5
        // production set plus the package's test-support and test assemblies.
        // SPEC §H2 item 7 names every OpWeave assembly other than
        // OpWeave.Pipeline, so test assemblies are in the scan too — a
        // missing entry would make the scan silently skip them.
        private static readonly string[] RequiredAssemblies =
        {
            "OpWeave.Core",
            "OpWeave.UnityAdapter",
            "OpWeave.Operations",
            "OpWeave.AgentApi",
            "OpWeave.Viewer",
            "OpWeave.Testing",
            "OpWeave.Core.Tests",
            "OpWeave.UnityAdapter.Tests",
        };

        // Inspected whenever compiled; all may legitimately be absent:
        // OpWeave.GraphToolkit needs Unity 6000.5 (SPEC §0.5),
        // OpWeave.Pipeline.Tests additionally needs com.unity.pipeline
        // (SPEC §H2 item 1), and OpWeave.Custom / OpWeave.Custom.Tests are the
        // user's custom-Operation assemblies (SPEC §M.2), which a project may
        // simply not define. OpWeave.Pipeline itself is the single allowed
        // exception and is never part of the scan set.
        private static readonly string[] ConditionalAssemblies =
        {
            "OpWeave.GraphToolkit",
            "OpWeave.Pipeline.Tests",
            "OpWeave.Custom",
            "OpWeave.Custom.Tests",
        };

        [Test]
        public void OpWeaveAssemblies_HaveNoPipelineReferences()
        {
            var inspected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in RequiredAssemblies)
            {
                var assembly = AssemblyReferenceInspector.FindAssembly(name);
                Assert.That(assembly, Is.Not.Null,
                    $"required OpWeave assembly {name} is not compiled");
                AssertNoPipelineReferences(assembly!, name);
                inspected.Add(name);
            }

            foreach (var name in ConditionalAssemblies)
            {
                var assembly = AssemblyReferenceInspector.FindAssembly(name);
                if (assembly is null)
                {
                    continue;
                }
                AssertNoPipelineReferences(assembly, name);
                inspected.Add(name);
            }

            // Every other loaded OpWeave.* assembly not in the named set is
            // inspected as well — an OpWeave assembly must not evade the rule
            // by being absent from a list maintained by hand. Only the
            // adapter itself is exempt; fixture assemblies are named
            // OpWeaveFixtures.* and never match the OpWeave. prefix.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (string.IsNullOrEmpty(name) ||
                    !name!.StartsWith("OpWeave.", StringComparison.Ordinal) ||
                    string.Equals(name, AdapterAssemblyName, StringComparison.Ordinal) ||
                    inspected.Contains(name))
                {
                    continue;
                }
                AssertNoPipelineReferences(assembly, name);
            }
            Assert.That(inspected.Count, Is.GreaterThanOrEqualTo(RequiredAssemblies.Length));
        }

        [Test]
        public void ViolatingFixture_PipelineReferenceIsDetected()
        {
            // The fixture is a production-style Editor assembly whose asmdef
            // references Unity.Pipeline and Unity.Pipeline.Attributes; the
            // same inspection must flag it. It compiles only when
            // com.unity.pipeline is installed (versionDefines +
            // defineConstraints, the same gate as OpWeave.Pipeline): without
            // the package the check is vacuously satisfied — reported as a
            // pass, never Inconclusive, which -runTests maps to a failed run.
            var fixture = AssemblyReferenceInspector.FindAssembly(ViolatingFixtureAssemblyName);
            if (fixture is null)
            {
                Assert.That(AssemblyReferenceInspector.FindAssembly(AdapterAssemblyName), Is.Null,
                    "OpWeave.Pipeline is compiled but the violating fixture is missing");
                Assert.Pass(
                    "com.unity.pipeline is not installed; the violating fixture does not compile");
            }
            Assert.That(PipelineReferences(fixture!), Is.Not.Empty,
                "the violating fixture must carry a Unity.Pipeline* reference for the check to flag");
        }

        private static void AssertNoPipelineReferences(Assembly assembly, string name)
        {
            var violations = PipelineReferences(assembly);
            Assert.That(violations, Is.Empty,
                $"{name} must not reference Unity.Pipeline* (SPEC §H2 item 7): " +
                string.Join(", ", violations));
        }

        private static List<string> PipelineReferences(Assembly assembly)
        {
            var violations = new List<string>();
            foreach (var reference in AssemblyReferenceInspector.ReferencedAssemblyNames(assembly))
            {
                if (reference.StartsWith(ForbiddenPrefix, StringComparison.Ordinal) &&
                    !violations.Contains(reference))
                {
                    violations.Add(reference);
                }
            }
            return violations;
        }
    }
}
