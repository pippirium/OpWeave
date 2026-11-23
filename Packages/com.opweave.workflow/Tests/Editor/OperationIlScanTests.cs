#nullable enable

// T-W2.10-03 [Edit] tests: local:T-W2.10-03:il-scan for the SPEC §M.6 item 3
// static inspection. The offending fixture assembly OpWeaveFixtures.IlScan
// under TestProject/Assets/OpWeaveFixtures/ carries four types, each holding
// exactly one reference to a SPEC §G0-prohibited API — a modal dialog
// (EditorUtility.DisplayDialog and
// EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo),
// AssetDatabase.StartAssetEditing and Task.Run — plus a clean type. The
// clean fixture assembly OpWeaveFixtures.Editor and the standard
// OpWeave.Operations assembly must produce no warning.
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using OpWeave.Operations;
using OpWeave.Testing;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The IL-scan acceptance checks: clean assemblies warn about nothing,
    /// and each offending fixture produces exactly one warning naming its
    /// type and method.
    /// </summary>
    internal sealed class OperationIlScanTests
    {
        private const string OffendingAssemblyName = "OpWeaveFixtures.IlScan";
        private const string CleanFixtureAssemblyName = "OpWeaveFixtures.Editor";

        // ── local:T-W2.10-03:il-scan — clean assemblies ────────────────────

        [Test]
        public void CleanAssembliesProduceNoWarnings()
        {
            // The clean fixture assembly.
            Assert.That(
                OperationIlScan.Scan(RequireAssembly(CleanFixtureAssemblyName)),
                Is.Empty,
                "the clean fixture assembly must produce no warning");

            // The standard Operations assembly — the scan's real subject:
            // no standard Operation may touch the prohibited APIs either.
            Assert.That(
                OperationIlScan.Scan(typeof(CreateFolderOperation).Assembly),
                Is.Empty,
                "the standard Operations assembly must produce no warning");

            // The Testing assembly itself stays clean as well.
            Assert.That(
                OperationIlScan.Scan(typeof(OperationIlScan).Assembly),
                Is.Empty,
                "the Testing assembly must produce no warning");
        }

        // ── local:T-W2.10-03:il-scan — offending fixture assembly ──────────

        [Test]
        public void EachOffendingFixtureProducesExactlyOneWarningNamingTypeAndMethod()
        {
            var warnings = OperationIlScan.Scan(RequireAssembly(OffendingAssemblyName));

            Expect(
                warnings, "UnityEditor.EditorUtility.DisplayDialog",
                "OpWeave.Fixtures.IlScanDisplayDialogFixture", "Offend");
            Expect(
                warnings,
                "UnityEditor.SceneManagement.EditorSceneManager." +
                    "SaveCurrentModifiedScenesIfUserWantsTo",
                "OpWeave.Fixtures.IlScanSaveScenesDialogFixture", "Offend");
            Expect(
                warnings, "UnityEditor.AssetDatabase.StartAssetEditing",
                "OpWeave.Fixtures.IlScanStartAssetEditingFixture", "Offend");
            Expect(
                warnings, "System.Threading.Tasks.Task.Run",
                "OpWeave.Fixtures.IlScanTaskRunFixture", "Offend");

            // Four fixture references, no more: the legal
            // AssetDatabase.StopAssetEditing beside the prohibited Start
            // call, and the clean fixture in the same assembly, add nothing.
            Assert.That(
                warnings,
                Has.Count.EqualTo(4),
                () => "unexpected warnings:\n" + Describe(warnings));
        }

        [Test]
        public void ScanByPathFindsTheSameWarnings()
        {
            var assembly = RequireAssembly(OffendingAssemblyName);
            var byPath = OperationIlScan.Scan(assembly.Location);

            Assert.That(byPath, Has.Count.EqualTo(4));
        }

        private static void Expect(
            IReadOnlyList<IlScanWarning> warnings,
            string api, string typeName, string methodName)
        {
            var matches = 0;
            IlScanWarning? found = null;
            foreach (var warning in warnings)
            {
                if (warning.Api == api &&
                    warning.TypeName == typeName &&
                    warning.MethodName == methodName)
                {
                    matches++;
                    found = warning;
                }
            }
            Assert.That(
                matches,
                Is.EqualTo(1),
                () => $"expected exactly one warning for {api} at " +
                    $"{typeName}.{methodName}:\n" + Describe(warnings));
            // The warning text carries the type and method for the report.
            Assert.That(found!.ToString(), Does.Contain(typeName));
            Assert.That(found.ToString(), Does.Contain(methodName));
        }

        private static Assembly RequireAssembly(string name)
        {
            var assembly = AssemblyReferenceInspector.FindAssembly(name);
            Assert.That(
                assembly, Is.Not.Null,
                $"fixture assembly {name} is not compiled");
            return assembly!;
        }

        private static string Describe(IReadOnlyList<IlScanWarning> warnings)
        {
            var builder = new StringBuilder();
            foreach (var warning in warnings)
            {
                builder.AppendLine(warning.ToString());
            }
            return builder.ToString();
        }
    }
}
