#nullable enable

// T-W2.9-05 [Edit] tests: local:T-W2.9-05:test-host-nunit — a candidate
// test asmdef qualifies only when it can compile the generated contract
// test (SPEC §M.4). The committed fixtures under Assets/OpWeaveFixtures/
// are otherwise-qualifying test asmdefs — Editor-only, gated on
// UNITY_INCLUDE_TESTS and referencing OpWeave.Testing plus every template
// reference — that still cannot compile the generated file: one drops
// nunit.framework.dll behind overrideReferences, the other strips the
// engine references via noEngineReferences (the generated [TearDown]
// calls UnityEditor's Undo.ClearAll). The planner must fall through to
// the first qualifying host instead of writing into the unqualified
// asmdef.
using NUnit.Framework;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.9-05:test-host-nunit — the SPEC §M.4 test-host
    /// qualification for the generated contract test.
    /// </summary>
    internal sealed class OperationCreateTestHostTests
    {
        private const string NoNUnitFolder =
            "Assets/OpWeaveFixtures/TestHostNoNUnit";
        private const string NoEngineRefsFolder =
            "Assets/OpWeaveFixtures/TestHostNoEngineRefs";

        // The ordinally first qualifying test host of TestProject — the
        // §M.2-shaped test asmdef of the OpWeave.Custom fixture.
        private const string QualifiedHostNamespace = "OpWeave.Custom.Tests";
        private const string ExpectedTestFile =
            "Assets/OpWeave/Editor/Operations/Transform/Tests/" +
            "TestHostProbeOperationTests.cs";

        private static OperationCreateRequest NewRequest(string targetFolder) =>
            new OperationCreateRequest
            {
                DisplayName = "Test Host Probe",
                TargetFolder = targetFolder,
            };

        // Acceptance: an otherwise-qualifying asmdef with
        // overrideReferences: true and no nunit.framework.dll precompiled
        // reference is not chosen as the test host — the defective
        // predicate would pick the fixture covering the target folder.
        [Test]
        public void Plan_TestHostWithoutNUnit_IsNotChosen()
        {
            var plan = OperationCreatePlanner.Plan(NewRequest(NoNUnitFolder));
            Assert.That(plan.HasErrors, Is.False,
                "unexpected issues: " + string.Join("; ", plan.Issues));
            Assert.That(plan.TestFilePath, Is.EqualTo(ExpectedTestFile));
            Assert.That(plan.TestNamespace,
                Is.EqualTo(QualifiedHostNamespace));
            Assert.That(plan.TestAsmdefPath, Is.Null);
        }

        // The same verdict for an asmdef whose noEngineReferences strips
        // the UnityEditor reference the generated [TearDown] needs.
        [Test]
        public void Plan_TestHostWithoutEngineReferences_IsNotChosen()
        {
            var plan = OperationCreatePlanner.Plan(
                NewRequest(NoEngineRefsFolder));
            Assert.That(plan.HasErrors, Is.False,
                "unexpected issues: " + string.Join("; ", plan.Issues));
            Assert.That(plan.TestFilePath, Is.EqualTo(ExpectedTestFile));
            Assert.That(plan.TestNamespace,
                Is.EqualTo(QualifiedHostNamespace));
        }
    }
}
