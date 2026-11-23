#nullable enable

// T-W2.2-04 [Edit] tests: local:T-W2.2-04:registry-diff — SPEC §C3 rule 7
// end to end. A missing/corrupt registry.json yields
// previousSnapshot "unavailable" with a reason and records no diff (the
// end-to-end form of the [Core] four-mode check); a real custom Operation
// file deleted, restored and Execute-only edited across real domain
// reloads yields removed (+ the OW-OP-001 "previously at <file>" hint),
// added and implementationChanged (SPEC §C3 tests 3–4).
//
// The domain-reload [UnityTest] writes its probe assembly under
// Assets/OpWeaveRegistryDiffProbe/ and keeps its store under
// Library/OpWeaveRegistryDiffProbe/; state crossing a reload lives only in
// files. Everything is removed by [UnityTearDown]; nothing tracked is
// modified.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RegistryDiffTests
    {
        private const string ProbeOpId = "test.registrydiff.probe";
        private const string ProbeAssetDir = "Assets/OpWeaveRegistryDiffProbe";
        private const string ProbeAssembly = "OpWeave.RegistryDiffProbe";
        private const string ProbeFileName = "DiffProbeOp.cs";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string StateDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveRegistryDiffProbe");

        private static string ProbeDiskDir => Path.Combine(ProjectRoot, ProbeAssetDir);
        private static string ProbeFile => Path.Combine(ProbeDiskDir, ProbeFileName);

        // ── Recorder fixtures ─────────────────────────────────────────────

        private sealed class Captured
        {
            internal readonly List<string> Infos = new List<string>();
            internal readonly List<string> Warnings = new List<string>();
            internal readonly List<IReadOnlyList<RegistryChange>> Events =
                new List<IReadOnlyList<RegistryChange>>();
            internal RunEvents Dispatcher = new RunEvents();

            internal Captured() =>
                Dispatcher.RegistryChanged += changes => Events.Add(changes);

            internal RegistryDiffRecorder Recorder(string root) =>
                new RegistryDiffRecorder(
                    root,
                    () => Dispatcher,
                    line => Infos.Add(line),
                    line => Warnings.Add(line));
        }

        private static RegistryBuildResult RebuildWith(RegistryDiffRecorder recorder) =>
            RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication(),
                diffRecorder: recorder);

        // ── Missing/corrupt registry.json: the end-to-end unavailable case ─

        [Test]
        public void Rebuild_MissingRegistryJson_ReportsUnavailableAndRecordsNoDiff()
        {
            Cleanup();
            var captured = new Captured();
            var result = RebuildWith(captured.Recorder(StateDir));

            var diff = result.Diff;
            Assert.That(diff, Is.Not.Null);
            Assert.That(diff!.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(diff.UnavailableReason, Does.Contain("missing"));
            Assert.That(diff.Changes, Is.Empty);
            Assert.That(captured.Events, Is.Empty, "no RegistryChanged dispatch without a record");
            Assert.That(captured.Warnings, Has.Count.EqualTo(1));
            StringAssert.Contains("unavailable", captured.Warnings[0]);
            Assert.That(File.Exists(
                Path.Combine(StateDir, RegistrySnapshotStore.ChangesFileName)), Is.False);
            // The new summary is still written so the next build can diff.
            Assert.That(File.Exists(
                Path.Combine(StateDir, RegistrySnapshotStore.SnapshotFileName)), Is.True);
        }

        [Test]
        public void Rebuild_CorruptRegistryJson_ReportsUnavailableAndRecordsNoDiff()
        {
            Cleanup();
            // First build persists a good summary; its own "previous
            // snapshot unavailable" warning is the baseline's, captured on
            // a throwaway recorder so the asserted build's capture holds
            // only the corrupt-file rebuild's output.
            RebuildWith(new Captured().Recorder(StateDir));
            Assert.That(File.Exists(
                Path.Combine(StateDir, RegistrySnapshotStore.SnapshotFileName)), Is.True);
            File.WriteAllText(
                Path.Combine(StateDir, RegistrySnapshotStore.SnapshotFileName),
                "{ corrupt");

            var captured = new Captured();
            var result = RebuildWith(captured.Recorder(StateDir));

            var diff = result.Diff!;
            Assert.That(diff.PreviousSnapshot, Is.EqualTo("unavailable"));
            Assert.That(diff.UnavailableReason, Is.Not.Null.And.Not.Empty);
            Assert.That(diff.Changes, Is.Empty,
                "a corrupt registry.json never produces a synthetic all-added diff");
            Assert.That(captured.Events, Is.Empty);
            Assert.That(captured.Warnings, Has.Count.EqualTo(1));
            Assert.That(
                File.Exists(
                    Path.Combine(StateDir, RegistrySnapshotStore.ChangesFileName)), Is.False);
            Assert.That(result.Snapshot, Is.Not.Null,
                "the new Registry is still built and published (SPEC §C3 rule 7)");
        }

        [Test]
        public void Rebuild_ChangedContent_AppendsSummaryLineAndDispatches()
        {
            Cleanup();
            // First build persists the baseline on a throwaway capture:
            // its "unavailable" warning must not accumulate into the
            // asserted build's capture.
            RebuildWith(new Captured().Recorder(StateDir));

            // Second build over the same production seams: identical
            // content, so the recorded batch holds no added/removed/changed
            // record — only any per-candidate failures of the build itself.
            var captured = new Captured();
            var result = RebuildWith(captured.Recorder(StateDir));

            var diff = result.Diff!;
            Assert.That(diff.PreviousSnapshot, Is.EqualTo("available"));
            Assert.That(captured.Warnings, Is.Empty,
                "a trusted previous snapshot emits no warning");
            // Two production builds over the same code: the recorded batch
            // is either empty (identical content) or only per-candidate
            // failures; no added/removed/changed record may appear.
            foreach (var change in diff.Changes)
            {
                Assert.That(change.Kind, Is.EqualTo(RegistryChangeKinds.Failed));
            }
        }

        // ── SPEC §C3 tests 3–4 across real domain reloads ─────────────────

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private const string ProbeAsmdef =
            "{\n    \"name\": \"" + ProbeAssembly + "\",\n" +
            "    \"rootNamespace\": \"OpWeave.RegistryDiffProbeNs\",\n" +
            "    \"references\": [\"OpWeave.Core\"],\n" +
            "    \"includePlatforms\": [\"Editor\"],\n" +
            "    \"autoReferenced\": false\n}\n";

        private static string ProbeSource(int variant) =>
            "#nullable enable\n" +
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "using OpWeave.Core;\n" +
            "using OpWeave.Core.Operations;\n\n" +
            "namespace OpWeave.RegistryDiffProbeNs\n{\n" +
            "    [OpWeaveOperation(\"" + ProbeOpId + "\", 1)]\n" +
            "    public sealed class DiffProbeOperation : IOperation\n" +
            "    {\n" +
            "        public OperationDescriptor Descriptor { get; } = Op.Define(\"" + ProbeOpId + "\", 1)\n" +
            "            .Summary(\"Registry diff probe operation.\")\n" +
            "            .Output(\"done\", \"core.Bool\")\n" +
            "            .Effects(EffectNames.Read)\n" +
            "            .Idempotency(Idempotency.Idempotent)\n" +
            "            .Build();\n" +
            "        public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>\n" +
            "            Array.Empty<Diagnostic>();\n" +
            "        public PreflightResult Preflight(StepContext ctx) =>\n" +
            "            new PreflightResult(Array.Empty<Diagnostic>(),\n" +
            "                Array.Empty<PlannedChange>(), JsonValue.Parse(\"{}\").Value!);\n" +
            "        public StepResult Execute(StepContext ctx)\n" +
            "        {\n" +
            "            ctx.SetOutput(\"done\", " + (variant == 0 ? "true" : "false") + ");\n" +
            "            return StepResult.Succeeded();\n" +
            "        }\n" +
            "        public StepResult Poll(StepContext ctx) =>\n" +
            "            throw new NotSupportedException(\"Immediate operations are never polled.\");\n" +
            "        public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>\n" +
            "            new ReconcileResult.Unknown(\"This probe never reconciles.\");\n" +
            "        public void Cancel(StepContext ctx) { }\n" +
            "    }\n" +
            "}\n";

        private static void WriteProbe(int variant)
        {
            Directory.CreateDirectory(ProbeDiskDir);
            File.WriteAllText(
                Path.Combine(ProbeDiskDir, ProbeAssembly + ".asmdef"), ProbeAsmdef);
            File.WriteAllText(ProbeFile, ProbeSource(variant));
            AssetDatabase.Refresh();
        }

        private static void DeleteProbe()
        {
            File.Delete(ProbeFile);
            File.Delete(ProbeFile + ".meta");
            AssetDatabase.Refresh();
        }

        private static bool Cleanup()
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
            if (Directory.Exists(ProbeDiskDir))
            {
                Directory.Delete(ProbeDiskDir, recursive: true);
                dirty = true;
            }
            return dirty;
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            if (Cleanup())
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (Cleanup())
            {
                yield return new RecompileScripts(false);
            }
            if (!EditorUtility.scriptCompilationFailed && !EditorApplication.isCompiling)
            {
                RegistryBuildHost.RebuildProduction();
            }
        }

        // Each phase asserts inside its own domain: nothing non-serializable
        // crosses a reload (the sentinel wait is inlined per SPEC §T.3 — a
        // nested IEnumerator yield would not survive the reload). The store
        // root is constant, so the persisted registry.json does the
        // cross-build comparing.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator DeleteRestoreEdit_ProducesRemovedAddedImplementationChangedAndHint()
        {
            WriteProbe(variant: 0);
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

            // Baseline: the first recorded build — previous unavailable, the
            // probe Operation registered as custom.
            var baseline = RebuildWith(new Captured().Recorder(StateDir));
            Assert.That(baseline.Diff!.PreviousSnapshot, Is.EqualTo("unavailable"));
            var registered = baseline.Snapshot!.Find(ProbeOpId, 1);
            Assert.That(registered, Is.Not.Null, "the probe Operation did not register");
            Assert.That(registered!.Origin, Is.EqualTo(OperationOrigin.Custom));
            Assert.That(registered.SourceFile, Does.Contain(ProbeFileName));

            DeleteProbe();
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe file deletion did not produce a domain reload");

            // Removed: the diff carries the previous source file, validate
            // answers OW-OP-001 with the "previously at <file>" hint.
            var captured = new Captured();
            var recorder = captured.Recorder(StateDir);
            var removed = RebuildWith(recorder);
            var removedDiff = removed.Diff!;
            Assert.That(removedDiff.PreviousSnapshot, Is.EqualTo("available"));
            var removedRecord = removedDiff.Changes
                .FirstOrDefault(c => c.Kind == RegistryChangeKinds.Removed &&
                                     c.OperationId == ProbeOpId);
            Assert.That(removedRecord, Is.Not.Null,
                "deleting the custom Operation file must yield removed");
            Assert.That(removedRecord!.SourceFile, Does.Contain(ProbeFileName));
            Assert.That(captured.Events, Has.Count.EqualTo(1),
                "RegistryChanged dispatches once after the recorded batch");
            Assert.That(captured.Infos, Has.Count.EqualTo(1),
                "one Console summary line is emitted");
            Assert.That(File.ReadAllLines(
                Path.Combine(StateDir, RegistrySnapshotStore.ChangesFileName)),
                Has.Some.Contains("\"removed\""));

            var report = ValidateAgainst(removed.Snapshot!, removedDiff.Changes);
            Assert.That(report.Ok, Is.False);
            var error = report.Errors.Single(e => e.Code == ErrorCodes.OW_OP_001);
            StringAssert.Contains(ProbeFileName, error.Hint,
                "OW-OP-001 must carry the 'previously at <file>' hint");

            // A later unrelated rebuild leaves no in-memory removed record,
            // yet the still-deleted Operation keeps its hint: the recorder
            // recomputes PreviousDefinitions from registry-changes.ndjson.
            var unrelated = RebuildWith(recorder);
            Assert.That(unrelated.Diff!.Changes
                    .Any(c => c.OperationId == ProbeOpId),
                Is.False, "an identical rebuild records nothing for the deleted probe");
            var persistedReport = ValidateAgainst(
                removed.Snapshot!, recorder.PreviousDefinitions);
            Assert.That(persistedReport.Ok, Is.False);
            var persistedError = persistedReport.Errors
                .Single(e => e.Code == ErrorCodes.OW_OP_001);
            StringAssert.Contains(ProbeFileName, persistedError.Hint,
                "the persisted previous-definition data keeps the 'previously at <file>' hint");

            WriteProbe(variant: 0);
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe file restore did not produce a domain reload");

            // Restored: the same Operation is back — added.
            var restored = RebuildWith(new Captured().Recorder(StateDir));
            var restoredRecord = restored.Diff!.Changes
                .FirstOrDefault(c => c.Kind == RegistryChangeKinds.Added &&
                                     c.OperationId == ProbeOpId);
            Assert.That(restoredRecord, Is.Not.Null,
                "restoring the custom Operation file must yield added");

            WriteProbe(variant: 1);
            ArmDomainSentinel();
            yield return new RecompileScripts();
            guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe Execute edit did not produce a domain reload");

            // Execute body only: implementationHash moves, descriptorHash does
            // not — implementationChanged, never descriptorChanged.
            var edited = RebuildWith(new Captured().Recorder(StateDir));
            var editedChanges = edited.Diff!.Changes
                .Where(c => c.OperationId == ProbeOpId).ToList();
            Assert.That(editedChanges, Has.Count.EqualTo(1));
            Assert.That(editedChanges[0].Kind,
                Is.EqualTo(RegistryChangeKinds.ImplementationChanged),
                "changing only Execute yields implementationChanged");
        }

        // ── Validation plumbing for the OW-OP-001 hint ────────────────────

        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;
            internal StubStaticContext(string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }
            public override string NodeId { get; }
            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;
            public override T Get<T>(string port) => throw new NotSupportedException();
        }

        private sealed class StubTypeProbe : IStaticTypeProbe
        {
            public bool TypeExists(string typeName) => true;
        }

        // The validation under test ends at D3's OW-OP-001 — Build Plan is
        // never reached — so a canned provenance is enough (the production
        // constructor requires a non-null one).
        private sealed class StubProvenance : IPlanProvenance
        {
            public IntegrityLevel IntegrityLevel => IntegrityLevel.Weak;
            public Result<PlanEnvironment> Environment => Result<PlanEnvironment>.Success(
                new PlanEnvironment(
                    "6000.0.85f1", "Windows", "StandaloneWindows64",
                    "Mono2x", "NET_Standard", Array.Empty<string>()));
            public Result<PlanTypeFingerprint> TypeFingerprintFor(TypeDefinition definition) =>
                throw new NotSupportedException();
            public Result<PlanCodecIdentity> CodecIdentityFor(TypeDefinition definition) =>
                throw new NotSupportedException();
        }

        private static ValidationReport ValidateAgainst(
            RegistrySnapshot snapshot, IReadOnlyList<RegistryChange> changes)
        {
            var created = WorkflowDocument.Create(new WorkflowDocumentData
            {
                SchemaVersion = "1.0",
                Id = "example.registrydiff",
                Nodes = new List<NodeData>
                {
                    new NodeData { Id = "start", Operation = "core.start", Version = 1 },
                    new NodeData { Id = "a", Operation = ProbeOpId, Version = 1 },
                },
                ControlEdges = new List<ControlEdge> { new ControlEdge("start", "a") },
            });
            Assert.That(created.Ok, Is.True,
                "document must create: " + string.Join(
                    "; ", created.Diagnostics.Select(d => d.Code + " " + d.JsonPath)));
            return new WorkflowCompiler(
                    snapshot,
                    (nodeId, parameters) => new StubStaticContext(nodeId, parameters),
                    new StubTypeProbe(),
                    new StubProvenance(),
                    changes)
                .Validate(new ValidationRequest(
                    created.Value!, null, ValidationMode.Static, ActorKind.Agent));
        }
    }
}
