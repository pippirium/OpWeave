#nullable enable

// T-W3.1-03 [Edit] tests: local:T-W3.1-03:describe-service — the SPEC §C8
// describe_operation service called directly over the live Registry (the
// production type + operation stages, so the standard catalog and the
// AT-01 proj.transform.reset custom Operation are the real registered
// ones). Every registered (id, version) is described and every returned
// example — fixture manifest applied through the ExampleFixture first —
// validates with ok:true and no warnings through the snapshot-backed
// WorkflowCompiler (SPEC §C8 test, AT-F06). SPEC §C1 item 4's "no Operation
// without a complete-WF Example" is a fail-closed contract over the live
// Registry: every registered Operation that publishes no Example names that
// Operation and its provenance and fails the pass — no population is
// filtered out and nothing is silently collected, this repository's
// compiled test support included. The defining-assembly rule below only
// labels test support in the reports; it never exempts it. The second fixture
// injects a deliberately broken custom Operation through the stage-1 seam:
// its describe answers OW-OP-001 with the registration failure's code and
// source file in the hint.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Search;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W3.1-03:describe-service — §C8 describe over the live
    /// Registry, every example validated with its fixture applied, and the
    /// broken-custom-Operation OW-OP-001 hint. The §C1 item-4 completeness
    /// rule is fail-closed over every Operation the live Registry registers —
    /// this repository's compiled test support included.
    /// </summary>
    internal sealed class DescribeServiceLiveRegistryTests
    {
        private const string BrokenId = "test.describe.broken.op";

        // The shipped contract SPEC §C1 item 4 governs: the standard catalog
        // of the OpWeave.Operations assembly and the AT-01 custom Operation of
        // the project's OpWeave.Custom assembly.
        private const string StandardOperationsAssembly = "OpWeave.Operations";
        private const string CustomOperationsAssembly = "OpWeave.Custom";
        private const string StandardOperationId = "core.start";
        private const string ResetOpId = "proj.transform.reset";

        // One documented identity rule labels this repository's compiled
        // test support in the pass's reports: an Operation whose defining
        // assembly carries the *.Tests test-assembly suffix
        // (OpWeave.Core.Tests, OpWeave.UnityAdapter.Tests,
        // OpWeave.Custom.Tests, every one compiled only under the
        // UNITY_INCLUDE_TESTS define constraint) and that is not defined
        // inside an extension package. SPEC F-12 keeps test support separate
        // from the production assemblies, so those probe classes are never
        // shipped.
        //
        // The label is reporting only — it is never an exemption: every
        // registered Operation, test support included, must publish a
        // complete-WF Example, and the compiled probes carry theirs through
        // ProbeOperationExample just like com.opweave.fixture.valid.op
        // carries its own.
        private const string TestAssemblySuffix = ".Tests";

        // The persistent embedded extension fixture whose Operation the §C1
        // item-4 population must judge (the F1 regression below).
        private const string FixtureOperationId = "com.opweave.fixture.valid.op";

        // The one documented test-support label of the §C1 item-4 pass.
        private static bool IsCompiledTestSupport(RegisteredOperation operation) =>
            (operation.PackageId is null ||
                string.Equals(operation.PackageId,
                    EditorAssemblyResolver.StandardPackageId, StringComparison.Ordinal)) &&
            operation.AssemblyName.EndsWith(TestAssemblySuffix, StringComparison.Ordinal);

        // The Registry's own provenance of one Operation: its defining
        // assembly, plus the UPM Package ID when it ships inside one.
        private static string ProvenanceOf(RegisteredOperation operation) =>
            operation.PackageId is { } packageId
                ? "assembly '" + operation.AssemblyName + "', package '" + packageId + "'"
                : "assembly '" + operation.AssemblyName + "'";

        // id@version of one registered Operation, as the pass labels it.
        private static string LabelOf(RegisteredOperation operation) =>
            operation.Descriptor.Id + "@" + operation.Descriptor.Version;

        // The D6 phase's required static context: the standard Operations'
        // ValidateStatic reads Parameters only — never Get<T> — so the stub
        // the per-descriptor tests use is safe for every node in an example.
        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;

            internal StubStaticContext(
                string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }

            public override string NodeId { get; }

            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException("No ValidateStatic reads a port.");
        }

        private static IEnumerator WaitForQuietEditor()
        {
            yield return new RecompileScripts(false, false);
            var guard = 0;
            while (EditorApplication.isCompiling && guard++ < 600)
            {
                yield return null;
            }
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the live registry cannot build over a failed script compile");
            Assert.IsFalse(EditorApplication.isCompiling,
                "the live registry cannot build while a compile is in flight");
        }

        private static RegistryBuildResult LiveBuild()
        {
            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale, "the live registry build must not be stale");
            Assert.IsNotNull(build.Snapshot, "the live registry must publish a snapshot");
            return build;
        }

        private static WorkflowCompiler Compiler(RegistryBuildResult build) =>
            new WorkflowCompiler(
                build.Snapshot!,
                (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                new UnityTypeProbe(),
                UnityPlanProvenance.Capture(IntegrityLevel.Weak, build.Providers));

        // SPEC §C8: describe every registered Operation and validate each
        // returned example with its declared fixture applied — through
        // this ticket's internal ExampleFixture, which materializes the
        // files and Prefab assets the manifest names and cleans them up on
        // Dispose. Every problem the pass finds is collected and reported
        // together: an assertion that stopped on the first Operation would
        // leave the rest of the Registry unchecked, which is not the
        // acceptance condition.
        [UnityTest]
        public IEnumerator EveryExampleOfEveryRegisteredOperationValidatesWithItsFixtureApplied()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var build = LiveBuild();
            var snapshot = build.Snapshot!;
            var service = new OperationDescribeService(snapshot);
            var compiler = Compiler(build);

            var described = 0;
            var validated = 0;
            var covered = 0;
            var coveredStandard = 0;
            var coveredLabels = new List<string>();
            var testSupportLabels = new List<string>();
            var problems = new List<string>();

            using (var fixture = new ExampleFixture())
            {
                foreach (var operation in snapshot.Operations)
                {
                    var descriptor = operation.Descriptor;
                    var label = LabelOf(operation);
                    var isTestSupport = IsCompiledTestSupport(operation);

                    var describe = service.DescribeOperation(
                        new DescribeOperationRequest(descriptor.Id)
                        {
                            Version = descriptor.Version,
                        });
                    if (!describe.Ok)
                    {
                        problems.Add(label + ": " + string.Join("; ",
                            describe.Diagnostics.Select(
                                d => d.Code + " " + d.Message)));
                        continue;
                    }
                    described++;
                    var description = describe.Value!;

                    if (description.RegistryGeneration != snapshot.GenerationId)
                    {
                        problems.Add(label +
                            ": the description must carry the answering generation");
                    }
                    // The test-support label only names the probes in the
                    // report below; it exempts nothing — every registered
                    // Operation is judged for a complete-WF Example.
                    if (!isTestSupport)
                    {
                        covered++;
                        coveredLabels.Add(label);
                        if (string.Equals(operation.AssemblyName, StandardOperationsAssembly,
                                StringComparison.Ordinal))
                        {
                            coveredStandard++;
                        }
                    }
                    else
                    {
                        testSupportLabels.Add(
                            label + " [" + ProvenanceOf(operation) + "]");
                    }

                    var examples = description.Examples;
                    if (examples is null)
                    {
                        problems.Add(label + ": the examples section must be populated");
                        continue;
                    }
                    if (examples.Count == 0)
                    {
                        problems.Add(label + ": an Operation registered without a " +
                            "complete-WF Example is incomplete (SPEC §C1 item 4): " +
                            ProvenanceOf(operation));
                    }

                    foreach (var example in examples)
                    {
                        if (example.FixtureManifest is { } manifest)
                        {
                            try
                            {
                                fixture.Apply(manifest);
                            }
                            catch (Exception ex)
                            {
                                problems.Add(label + " example '" + example.Title +
                                    "': its declared fixtureManifest could not be applied: " +
                                    ex.GetType().Name + ": " + ex.Message);
                                continue;
                            }
                        }

                        var read = WorkflowSerializer.Read(
                            example.WorkflowJson.CanonicalText);
                        if (!read.Ok)
                        {
                            problems.Add(label + " example '" + example.Title + "': " +
                                string.Join("; ", read.Diagnostics.Select(
                                    d => d.Code + " " + d.Message)));
                            continue;
                        }

                        var report = compiler.Validate(new ValidationRequest(
                            read.Value!, null, ValidationMode.Static, ActorKind.Agent));
                        if (!report.Ok)
                        {
                            problems.Add(label + " example '" + example.Title + "': " +
                                string.Join("; ", report.Errors.Select(
                                    d => d.Code + " " + d.Message + " " + d.JsonPath)));
                        }
                        if (report.Warnings.Count != 0)
                        {
                            problems.Add(label + " example '" + example.Title + "': " +
                                string.Join("; ", report.Warnings.Select(
                                    d => d.Code + " " + d.Message + " " + d.JsonPath)));
                        }
                        if (report.Ok && report.Warnings.Count == 0)
                        {
                            validated++;
                        }
                    }
                }
            }

            Assert.That(problems, Is.Empty, () =>
                problems.Count + " problem(s) across " + described +
                " described Operations (" + covered + " shipped-catalog, custom " +
                "and extension Operations under SPEC §C1 item 4; " +
                testSupportLabels.Count + " compiled test-support probes judged " +
                "the same way: " + string.Join(", ", testSupportLabels) +
                "):\n" + string.Join("\n", problems));
            Assert.Greater(described, 0, "the live registry must register Operations");
            Assert.Greater(validated, 0,
                "the live registry must publish at least one example");
            Assert.That(covered, Is.GreaterThan(30), () =>
                "SPEC §C1 item 4 must cover the shipped catalog, the AT-01 custom " +
                "Operation and every registered extension, not only the compiled " +
                "test support (" + testSupportLabels.Count +
                " probes registered from *.Tests assemblies)");
            Assert.Greater(coveredStandard, 0,
                StandardOperationsAssembly + "'s Operations are inside the " +
                "§C1 item-4 population");
            Assert.That(coveredLabels, Does.Contain(ResetOpId + "@1"),
                "the AT-01 custom Operation is inside the §C1 item-4 population");
        }

        // F1 regression (T-W3.1-03 corrections 2 and 3). com.opweave.fixture.valid.op@1
        // is no leak installed by another test: com.opweave.fixture.valid is
        // embedded in TestProject/Packages, so its Editor assembly compiles
        // whenever the project loads and the production stages register that
        // Operation on every live build — which
        // ExtensionRegistrationGateTests.ValidExtension_OperationAndProviderRegister
        // asserts. Being registered on every suite run, it has to carry its own
        // complete-WF Example
        // (TestProject/Packages/com.opweave.fixture.valid/Editor/ValidOperation.cs).
        // On head e57e481 that Operation published no Example at all and the
        // harness-provenance exemption classified its fixture provenance as test
        // support, so the pass collected it silently: run this test on that head
        // and both the Example assertion and the fail-closed assertion below
        // fail and name it, while the label assertion above shows the
        // exemption — not the identity rule — is what hid it.
        //
        // Correction 3 removes the same exemption from the fail-closed query
        // itself: on head 0405300 the compiled probe Operations of the *.Tests
        // assemblies — test.registry.good@1, test.dummy.immediate@1 and their
        // siblings — registered on every live build with no Example at all,
        // yet the query filtered them out, so the assertion below passed while
        // registered Operations published nothing. With the filter removed it
        // fails on 0405300's logic and names every one of them, because the
        // unfiltered set contains those example-less probes; now that each
        // probe publishes a complete-WF Example through ProbeOperationExample,
        // the assertion holds for the whole registered set.
        [UnityTest]
        public IEnumerator LiveRegistryJudgedPopulation_NeverRegistersAnOperationWithoutAnExample()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var snapshot = LiveBuild().Snapshot!;

            var fixtureOperation = snapshot.Find(FixtureOperationId, 1);
            Assert.That(fixtureOperation, Is.Not.Null,
                "the live Registry registers the extension fixture Operation of T-W2.12-06");
            Assert.That(IsCompiledTestSupport(fixtureOperation!), Is.False,
                FixtureOperationId + " is defined inside an extension package, so the " +
                "test-support boundary never reaches it and it is judged by §C1 item 4: " +
                ProvenanceOf(fixtureOperation!));
            Assert.That(fixtureOperation!.Descriptor.Examples, Is.Not.Empty, () =>
                FixtureOperationId + " registers on every suite run from the persistent " +
                "embedded package, so SPEC §C1 item 4 requires its own complete-WF " +
                "Example: " + ProvenanceOf(fixtureOperation!));

            var standard = snapshot.Find(StandardOperationId, 1);
            Assert.That(standard, Is.Not.Null,
                StandardOperationId + " must register in the live Registry");
            Assert.That(standard!.AssemblyName, Is.EqualTo(StandardOperationsAssembly));
            Assert.That(IsCompiledTestSupport(standard!), Is.False,
                "a standard catalog Operation is never compiled test support");
            Assert.That(standard!.Descriptor.Examples, Is.Not.Empty,
                StandardOperationId + " publishes the complete-WF Example §C1 item 4 requires");

            var custom = snapshot.Find(ResetOpId, 1);
            Assert.That(custom, Is.Not.Null,
                "the live Registry registers the AT-01 custom Operation");
            Assert.That(custom!.AssemblyName, Is.EqualTo(CustomOperationsAssembly));
            Assert.That(IsCompiledTestSupport(custom!), Is.False,
                "the AT-01 custom Operation is never compiled test support");
            Assert.That(custom!.Descriptor.Examples, Is.Not.Empty,
                ResetOpId + " publishes the complete-WF Example §C1 item 4 requires");

            // Fail-closed over the whole registered set: nothing registered
            // may be collected without an Example — no population is filtered
            // out, test support or not. On 0405300's logic this query filtered
            // the compiled probes out and passed while example-less
            // Operations like test.registry.good@1 stayed registered;
            // unfiltered it fails there and names them all.
            var exampleLess = snapshot.Operations
                .Where(o => o.Descriptor.Examples.Count == 0)
                .Select(o => LabelOf(o) + " [" + ProvenanceOf(o) + "]")
                .ToList();
            Assert.That(exampleLess, Is.Empty, () =>
                "every registered Operation must publish a complete-WF Example " +
                "(SPEC §C1 item 4): " + string.Join(", ", exampleLess));

            // The test-support label is reported, never silent and never an
            // exemption: it names the probe Operations of the compiled test
            // assemblies so the query above could not hide one.
            var testSupport = snapshot.Operations
                .Where(o => IsCompiledTestSupport(o))
                .Select(o => LabelOf(o) + " [" + ProvenanceOf(o) + "]")
                .ToList();
            Assert.That(testSupport, Is.Not.Empty, () =>
                "the test-support label must identify the probe Operations of " +
                "the compiled *.Tests assemblies — they are judged by the " +
                "example rule like everything else (" +
                string.Join(", ", testSupport) + ")");
        }

        // The deliberately broken custom Operation: its Descriptor names a
        // port type nothing registers, so the registry build refuses it —
        // the kind of failure SPEC §C8 makes describe surface. The
        // [OpWeaveOperation] attribute's caller-file provenance is the
        // source file the failure record carries.
        [OpWeaveOperation(BrokenId, 1)]
        internal sealed class BrokenFixtureOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } =
                Op.Define(BrokenId, 1)
                    .Summary("A describe fixture whose port type never registers.")
                    .Input("value", "test.describe.missing.type", required: true)
                    .Build();

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        private static RegistryTypeStage.Seams BrokenSeams() =>
            new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => new[] { typeof(BrokenFixtureOperation) },
                ProviderTypes: () =>
                    TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: type => EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());

        // SPEC §C8: an ID whose registration failed answers OW-OP-001 with
        // the failure's reason — the published code and the source file —
        // in the hint.
        [Test]
        public void BrokenCustomOperation_DescribeReturnsOwOp001_WithCodeAndSourceFileInHint()
        {
            var build = RegistryBuildHost.Rebuild(
                BrokenSeams(), RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale, "the fixture registry build must not be stale");
            Assert.IsNotNull(build.Snapshot, "the build must publish a snapshot");
            var snapshot = build.Snapshot!;

            Assert.IsNull(snapshot.FindLatest(BrokenId),
                "the broken Operation must never reach the registered set");

            var failure = snapshot.Failures
                .Where(f => f.OperationId == BrokenId &&
                    f.Kind == RegistryChangeKinds.Failed)
                .ToList();
            Assert.That(failure, Has.Count.EqualTo(1),
                "the rejection must land on the published failure record");
            Assert.IsNotEmpty(failure[0].Diagnostics,
                "the rejection must carry the diagnostic the hint names");
            Assert.IsNotNull(failure[0].SourceFile,
                "the rejection must carry the candidate's source file");

            var expectedFile = failure[0].SourceFile!;

            var result = new OperationDescribeService(snapshot)
                .DescribeOperation(new DescribeOperationRequest(BrokenId));

            Assert.IsFalse(result.Ok, "a failed-registration ID must not describe");
            var diagnostic = result.Diagnostics[0];
            Assert.That(diagnostic.Code, Is.EqualTo(ErrorCodes.OW_OP_001));
            foreach (var code in failure[0].Diagnostics
                .Where(d => d.Code.Length != 0)
                .Select(d => d.Code))
            {
                Assert.That(diagnostic.Hint, Does.Contain(code),
                    "the hint must carry the registration failure's code");
            }
            Assert.That(diagnostic.Hint, Does.Contain(expectedFile),
                "the hint must carry the registration failure's source file");
        }
    }
}
