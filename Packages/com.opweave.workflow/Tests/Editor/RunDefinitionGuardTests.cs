#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using OpWeave.Operations;
using UnityEditor;
using UnityEngine;
using CompilationAssembly = UnityEditor.Compilation.Assembly;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// [Edit] tests of the SPEC §D0.3 definition-mutation guard
    /// (local:T-W4.2-06:self-modification-guard): target classification
    /// against the protected-definition set of unfinished Runs — scripts
    /// and precompiled DLLs named after used assemblies, .asmdef/.asmref
    /// boundary writes, package ids, the OpWeave package itself, the
    /// fail-closed undecidable verdicts, the unrelated-asset allowance and
    /// the pre-side-effect recheck that catches a Run accepted after
    /// Preflight. The Unity surfaces are replaced by a
    /// <see cref="RunDefinitionGuardQueries"/> fake so no real asset is
    /// touched.
    /// </summary>
    internal sealed class RunDefinitionGuardTests
    {
        private const string WorkflowId = "example.guard";

        private readonly List<IDisposable> _seams = new List<IDisposable>();

        [TearDown]
        public void TearDown()
        {
            foreach (var seam in _seams)
            {
                seam.Dispose();
            }
            _seams.Clear();
        }

        // ── Fixtures ────────────────────────────────────────────────────

        private sealed class FakeQueries : RunDefinitionGuardQueries
        {
            internal readonly Dictionary<string, string?> ScriptAssembly =
                new Dictionary<string, string?>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string?> FileText =
                new Dictionary<string, string?>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> Guids =
                new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, IReadOnlyList<string>?> Dirs =
                new Dictionary<string, IReadOnlyList<string>?>(StringComparer.Ordinal);
            internal readonly HashSet<string> Folders =
                new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<CompilationAssembly> Compiled =
                new List<CompilationAssembly>();
            internal readonly List<GuardPackageInfo> Packages =
                new List<GuardPackageInfo>();
            internal bool PackagesUnavailable;
            internal string[] Precompiled = Array.Empty<string>();
            internal readonly Dictionary<string, string?> PrecompiledPaths =
                new Dictionary<string, string?>(StringComparer.Ordinal);
            internal string? DefaultAssembly;

            internal override string? AssemblyOfScript(string assetPath) =>
                ScriptAssembly.TryGetValue(assetPath, out var assembly)
                    ? assembly
                    : DefaultAssembly;
            internal override CompilationAssembly[] EditorAssemblies() =>
                Compiled.ToArray();
            internal override string[] PrecompiledNames() => Precompiled;
            internal override string? PrecompiledPath(string fileName) =>
                PrecompiledPaths.TryGetValue(fileName, out var path) ? path : null;
            internal override bool IsValidFolder(string assetPath) =>
                Folders.Contains(assetPath);
            internal override string? GuidToAssetPath(string guid) =>
                Guids.TryGetValue(guid, out var path) ? path : null;
            internal override string? ReadFileText(string assetPath) =>
                FileText.TryGetValue(assetPath, out var text) ? text : null;
            internal override IReadOnlyList<string>? EnumerateFiles(string assetDir) =>
                Dirs.TryGetValue(assetDir, out var files)
                    ? files
                    : Array.Empty<string>();
            internal override IReadOnlyList<GuardPackageInfo>? RegisteredPackages() =>
                PackagesUnavailable ? null : Packages;

            // The registry keeps one record per package name — re-registering
            // replaces the record the way re-resolving a manifest entry does.
            internal void RegisterPackage(string name, params string[] dependencies)
            {
                var index = Packages.FindIndex(
                    info => string.Equals(info.Name, name, StringComparison.Ordinal));
                if (index >= 0)
                {
                    Packages.RemoveAt(index);
                }
                Packages.Add(new GuardPackageInfo(name, null, dependencies));
            }
        }

        private sealed class Harness
        {
            internal readonly FakeQueries Queries = new FakeQueries();
            internal readonly List<RegisteredOperation> Operations =
                new List<RegisteredOperation>();
            internal List<ProtectedRun> Runs = new List<ProtectedRun>();
            internal RegistrySnapshot? Registry;

            internal Harness()
            {
                Registry = Snapshot();
            }

            internal RegistrySnapshot Snapshot() =>
                new RegistrySnapshot(
                    "reg_01J00000000000000000000000",
                    new string('f', 64),
                    Operations,
                    RunnerHostFixture.Types,
                    Array.Empty<RegistryChange>(),
                    Array.Empty<TypeRegistryFailure>());

            internal IDisposable Install() =>
                RunDefinitionGuard.InstallForTests(
                    () => Runs,
                    () => Registry,
                    Queries);

            internal void Protect(string runId, CompiledPlan plan)
            {
                Runs.Add(new ProtectedRun(runId, plan));
            }
        }

        private Harness NewHarness()
        {
            var harness = new Harness();
            _seams.Add(harness.Install());
            return harness;
        }

        private static string Hash64(char c) => new string(c, 64);

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {text}");
            return parsed.Value!;
        }

        private static RegisteredOperation Operation(
            string id, int version, string assembly, string? package = null) =>
            new RegisteredOperation(
                Op.Define(id, version).Summary("test operation").Build(),
                OperationOrigin.Standard, assembly,
                null, Hash64('d'), Hash64('e'))
            {
                PackageId = package,
            };

        private static PlanStep Step(
            int index, string nodeId, string operationId, int version) =>
            new PlanStep(
                index, nodeId, operationId, version, Hash64('d'), Hash64('e'),
                new Dictionary<string, InputSource>(),
                new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = -1 },
                Array.Empty<string>(),
                30, new RetryPolicy(), Idempotency.Unsafe);

        private static CompiledPlan Plan(
            PlanStep[]? steps = null,
            string? usedTypeFingerprints = "[]",
            string? usedCodecFingerprints = "[]")
        {
            var fingerprintMembers =
                ",\"usedCodecFingerprints\":" + (usedCodecFingerprints ?? "[]") +
                ",\"usedTypeFingerprints\":" + (usedTypeFingerprints ?? "[]");
            var hashInput = Json(
                "{\"canonicalJsonVersion\":1,\"environmentIdentifiers\":{}," +
                "\"executionInputs\":{},\"requiredEffects\":[]," +
                "\"resolvedWorkflow\":{\"id\":\"" + WorkflowId + "\"},\"retry\":[]," +
                "\"steps\":[],\"timeouts\":[],\"transitions\":[]," +
                "\"usedOperationFingerprints\":[]" + fingerprintMembers + "}");
            return new CompiledPlan(
                "plan_01J00000000000000000000000",
                Hash64('a'), WorkflowId, Hash64('b'), Hash64('c'),
                Json("{}"),
                steps ?? new[] { Step(0, "a", "asset.move", 1) },
                Array.Empty<PlanOutput>(),
                Array.Empty<string>(), new List<ApprovalRequirement>(),
                Array.Empty<PlanSourceMapEntry>(), hashInput);
        }

        private static void AssertRefused(Diagnostic? diagnostic, string because = "")
        {
            Assert.IsNotNull(diagnostic, "expected OW-RUN-013 " + because);
            Assert.AreEqual(ErrorCodes.OW_RUN_013, diagnostic!.Code, because);
        }

        // A run whose Plan uses one Operation in "Protected.Ops" shipped by
        // the package "com.protected.ops". The fixture registers that package
        // in the resolved registry too: a protected package the registry does
        // not describe leaves the dependency direction of ANY package
        // mutation unprovable (SPEC §D0.3 item 2 fails closed), so without
        // this record no package case could ever be decided as unrelated.
        private static void AddProtectedRun(Harness harness, string runId)
        {
            harness.Operations.Add(
                Operation("asset.move", 1, "Protected.Ops", "com.protected.ops"));
            harness.Registry = harness.Snapshot();
            harness.Protect(runId, Plan());
            harness.Queries.Compiled.Add(Compiled(
                "Protected.Ops", "Packages/com.protected.ops/Editor/Op.cs"));
            harness.Queries.RegisterPackage("com.protected.ops");
        }

        private static CompilationAssembly Compiled(
            string name, params string[] sourceFiles) =>
            new CompilationAssembly(
                name,
                "Library/ScriptAssemblies/" + name + ".dll",
                sourceFiles,
                new string[0],
                new CompilationAssembly[0],
                new string[0],
                UnityEditor.Compilation.AssemblyFlags.None);

        // ── SPEC §D0.3 item 3 — path classification ─────────────────────

        // No unfinished Runs: every check is a pass-through.
        [Test]
        public void GuardAllowsAnythingWhenNoRunsAreUnfinished()
        {
            NewHarness();
            Assert.IsNull(RunDefinitionGuard.CheckPath(
                "Assets/Scripts/Thing.cs", GuardedPathRole.Destination));
            Assert.IsNull(RunDefinitionGuard.CheckPackageMutation("com.anything"));
            Assert.IsNull(RunDefinitionGuard.CheckUnprovableDefinitionChange("sweep"));
            Assert.IsFalse(RunDefinitionGuard.HasProtectedRuns);
        }

        // A script write inside a protected Run's used-Operation assembly is
        // refused with OW-RUN-013 before any side effect.
        [Test]
        public void ScriptInProtectedAssemblyIsRefused()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.ScriptAssembly["Assets/Tools/Thing.cs"] = "Protected.Ops";

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Tools/Thing.cs", GuardedPathRole.Destination);

            AssertRefused(diagnostic);
            StringAssert.Contains("run_a", diagnostic!.Hint);
        }

        // A script inside an unrelated compiled assembly is unrelated and
        // allowed (SPEC §D0.3: unrelated DLL/asset changes proceed).
        [Test]
        public void ScriptInAnUnrelatedAssemblyIsAllowed()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.ScriptAssembly["Assets/Scripts/Thing.cs"] =
                "Assembly-CSharp";
            harness.Queries.Compiled.Add(
                Compiled("Assembly-CSharp", "Assets/Scripts/Thing.cs"));

            Assert.IsNull(RunDefinitionGuard.CheckPath(
                "Assets/Scripts/Thing.cs", GuardedPathRole.Destination));
        }

        // The whole defining DLL is protected — a precompiled DLL carrying
        // the protected name is refused whatever its classes do.
        [Test]
        public void PrecompiledDllNamedAfterAProtectedAssemblyIsRefused()
        {
            var harness = NewHarness();
            harness.Operations.Add(
                Operation("asset.move", 1, "Foreign.Plugin", "com.protected.ops"));
            harness.Registry = harness.Snapshot();
            harness.Protect("run_a", Plan());
            harness.Queries.Precompiled = new[] { "Foreign.Plugin.dll" };
            harness.Queries.PrecompiledPaths["Foreign.Plugin.dll"] =
                "Assets/Plugins/Foreign.Plugin.dll";

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Plugins/Foreign.Plugin.dll", GuardedPathRole.Source);

            AssertRefused(diagnostic);
        }

        // An unrelated DLL name is allowed.
        [Test]
        public void AnUnrelatedDllIsAllowed()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");

            Assert.IsNull(RunDefinitionGuard.CheckPath(
                "Assets/Plugins/Other.Plugin.dll", GuardedPathRole.Source));
        }

        // A used Type/Codec defining assembly from the persisted Plan
        // fingerprints is protected even though no Plan step names it.
        [Test]
        public void UsedTypeDefiningAssemblyIsProtected()
        {
            var harness = NewHarness();
            harness.Operations.Add(Operation("asset.move", 1, "Protected.Ops"));
            harness.Registry = harness.Snapshot();
            var plan = Plan(
                usedTypeFingerprints:
                    "[{\"id\":\"example.T\",\"version\":1,\"fingerprintHash\":\"" + Hash64('1') +
                    "\",\"definingAssemblyName\":\"Protected.Types\"}]");
            harness.Protect("run_types", plan);
            harness.Queries.Compiled.Add(
                Compiled("Protected.Ops", "Packages/com.protected.ops/Op.cs"));
            harness.Queries.Compiled.Add(
                Compiled("Protected.Types", "Assets/Types/T.cs"));
            harness.Queries.ScriptAssembly["Assets/T.cs"] = "Protected.Types";

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/T.cs", GuardedPathRole.Source);

            AssertRefused(diagnostic);
            StringAssert.Contains("run_types", diagnostic!.Hint);
        }

        // SPEC §D0.3 item 2: the OpWeave package itself is protected for
        // every unfinished Run.
        [Test]
        public void OpWeavePackagePathsAreRefused()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");

            var pathDiagnostic = RunDefinitionGuard.CheckPath(
                "Packages/" + EditorAssemblyResolver.StandardPackageId +
                    "/Editor/Core/Runtime/RunCoordinator.cs",
                GuardedPathRole.Source);
            var packageDiagnostic = RunDefinitionGuard.CheckPackageMutation(
                EditorAssemblyResolver.StandardPackageId);

            AssertRefused(pathDiagnostic);
            AssertRefused(packageDiagnostic);
        }

        // An .asmdef whose declared name collides with a protected assembly
        // is a definition change into a protected assembly.
        [Test]
        public void IncomingAsmdefCollidingWithAProtectedAssemblyIsRefused()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.DefaultAssembly = "Assembly-CSharp";
            harness.Queries.FileText["Assets/Sub/New.asmdef"] =
                "{\"name\":\"Protected.Ops\"}";

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Sub/New.asmdef", GuardedPathRole.Destination,
                contentPath: "Assets/Sub/New.asmdef");

            AssertRefused(diagnostic);
        }

        // A normal asset — never part of a compiled assembly — is provably
        // unrelated even when a Run's protected set is unprovable.
        [Test]
        public void NormalAssetsPassEvenWhenARunIsUncertain()
        {
            var harness = NewHarness();
            // Registry knows nothing of "asset.move" → the Plan's used
            // Operation cannot be resolved → Uncertain.
            harness.Protect("run_uncertain", Plan());

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Textures/Logo.png", GuardedPathRole.Source);

            Assert.IsNull(diagnostic);
        }

        // ── SPEC §D0.3 item 3 — fail-closed verdicts ────────────────────

        // An uncertain protected set refuses every definition-bearing
        // change (SPEC §D0.3 item 3).
        [Test]
        public void UncertainRunRefusesDefinitionChanges()
        {
            var harness = NewHarness();
            harness.Protect("run_uncertain", Plan());
            harness.Queries.ScriptAssembly["Assets/Thing.cs"] = "Assembly-CSharp";

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Thing.cs", GuardedPathRole.Destination);

            AssertRefused(diagnostic);
            StringAssert.Contains("run_uncertain", diagnostic!.Hint);
        }

        // A script whose owning assembly cannot be established is
        // undecidable while Runs are unfinished.
        [Test]
        public void UnresolvableScriptDomainIsUndecidable()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            // ScriptAssembly has no entry and no default → null.

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Nowhere/Thing.cs", GuardedPathRole.Destination);

            AssertRefused(diagnostic);
        }

        // A protected assembly absent from both the compiled and the
        // precompiled picture leaves subtree checks unprovable.
        [Test]
        public void UnmappedProtectedAssemblyFailsClosed()
        {
            var harness = NewHarness();
            harness.Operations.Add(Operation("asset.move", 1, "Ghost.Ops"));
            harness.Registry = harness.Snapshot();
            harness.Protect("run_ghost", Plan());
            harness.Queries.DefaultAssembly = "Assembly-CSharp";
            harness.Queries.FileText["Assets/Sub/x.rsp"] = "-define:X";

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Sub/x.rsp", GuardedPathRole.Destination);

            AssertRefused(diagnostic);
        }

        // An unprovable sweep — pending asset saves — refuses while any
        // Run is unfinished.
        [Test]
        public void UnprovableSweepIsRefused()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");

            AssertRefused(RunDefinitionGuard.CheckUnprovableDefinitionChange(
                "pending asset saves"));
        }

        // ── SPEC §D0.3 item 3 — package classification ──────────────────

        // Removing the package a used Operation ships in is refused.
        [Test]
        public void RemovingAUsedOperationPackageIsRefused()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");

            var diagnostic =
                RunDefinitionGuard.CheckPackageMutation("com.protected.ops");

            AssertRefused(diagnostic);
            StringAssert.Contains("run_a", diagnostic!.Hint);
        }

        // Removing a package nothing protected depends on is allowed — with
        // the whole package picture resolved, so the allowance is the SPEC
        // §D0.3 item 2 "proven unrelated" verdict and not missing evidence.
        [Test]
        public void RemovingAnUnrelatedPackageIsAllowed()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            // The protected package has a real dependency; it is not the
            // removal target, so com.unrelated.tools stays provably
            // unrelated.
            harness.Queries.RegisterPackage(
                "com.protected.ops", "com.shared.library");
            harness.Queries.RegisterPackage("com.shared.library");
            harness.Queries.RegisterPackage("com.unrelated.tools");

            Assert.IsNull(
                RunDefinitionGuard.CheckPackageMutation("com.unrelated.tools"));
        }

        // A protected package the resolved registry does not describe has
        // unreadable dependency declarations, so no other package is
        // provably unrelated — even an unrelated-looking removal is refused
        // (SPEC §D0.3 item 2/3 fail closed).
        [Test]
        public void RemovingAPackageIsRefusedWhenTheProtectedPackageIsUnresolved()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.Packages.Clear();
            harness.Queries.RegisterPackage("com.unrelated.tools");

            var diagnostic =
                RunDefinitionGuard.CheckPackageMutation("com.unrelated.tools");

            AssertRefused(diagnostic);
            StringAssert.Contains("com.unrelated.tools", diagnostic!.Hint);
            StringAssert.Contains("run_a", diagnostic.Hint);
        }

        // The registry itself cannot be captured — the dependency direction
        // of every package is unknown, so the same removal is refused.
        [Test]
        public void RemovingAPackageIsRefusedWhenThePackageRegistryIsUnreadable()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.RegisterPackage("com.unrelated.tools");
            harness.Queries.PackagesUnavailable = true;

            var diagnostic =
                RunDefinitionGuard.CheckPackageMutation("com.unrelated.tools");

            AssertRefused(diagnostic);
            StringAssert.Contains("run_a", diagnostic!.Hint);
        }

        // Removing a dependency a protected package declares breaks the
        // protected package — refused.
        [Test]
        public void RemovingAProtectedPackagesDependencyIsRefused()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.RegisterPackage(
                "com.protected.ops", "com.shared.library");

            var diagnostic =
                RunDefinitionGuard.CheckPackageMutation("com.shared.library");

            AssertRefused(diagnostic);
        }

        // ── SPEC §D0.3 item 4 — the pre-side-effect recheck ─────────────

        // The serialized-recheck guarantee: a Run accepted between an
        // Operation's Preflight and its final check is caught by the second
        // check — nothing slips between acceptance and the side effect.
        [Test]
        public void RunAcceptedAfterPreflightIsCaughtByTheRecheck()
        {
            var harness = NewHarness();
            harness.Queries.ScriptAssembly["Assets/Thing.cs"] = "Protected.Ops";

            var preflight = RunDefinitionGuard.CheckPath(
                "Assets/Thing.cs", GuardedPathRole.Destination);
            Assert.IsNull(preflight, "no protected Runs yet — Preflight passes");

            // A Run is accepted (main thread) before the side effect runs.
            AddProtectedRun(harness, "run_late");

            var recheck = RunDefinitionGuard.CheckPath(
                "Assets/Thing.cs", GuardedPathRole.Destination);
            AssertRefused(recheck, "the final check sees the accepted Run");
            StringAssert.Contains("run_late", recheck!.Hint);
        }

        // ── Incoming trees (folder copy/move) ──────────────────────────

        // A folder move carrying scripts into a protected governing domain
        // is refused at the destination.
        [Test]
        public void FolderMoveIntoAProtectedDomainIsRefused()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.Dirs["Assets/Source"] = new List<string>
            {
                "Assets/Source/Thing.cs",
            };
            harness.Queries.ScriptAssembly["Assets/Dest/Thing.cs"] =
                "Protected.Ops";

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Dest", GuardedPathRole.Destination, isDirectory: true,
                contentPath: "Assets/Source");

            AssertRefused(diagnostic);
        }

        // Scripts an incoming .asmref carves out join the referenced
        // assembly — a reference to a protected assembly refuses the move.
        [Test]
        public void IncomingAsmrefRepointingAtAProtectedAssemblyIsRefused()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.DefaultAssembly = "Assembly-CSharp";
            harness.Queries.Dirs["Assets/Source"] = new List<string>
            {
                "Assets/Source/Boundary.asmref",
                "Assets/Source/Thing.cs",
            };
            harness.Queries.FileText["Assets/Source/Boundary.asmref"] =
                "{\"reference\":\"Protected.Ops\"}";

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Dest", GuardedPathRole.Destination, isDirectory: true,
                contentPath: "Assets/Source");

            AssertRefused(diagnostic);
        }

        // ── Operation wiring: Preflight and the pre-side-effect recheck ──

        private static readonly IReadOnlyDictionary<string, string> ScriptInputTypes =
            new Dictionary<string, string>
            {
                ["path"] = "unity.AssetPath",
                ["content"] = "core.String",
                ["overwrite"] = "core.Bool",
            };

        private static readonly IReadOnlyDictionary<string, string> ScriptOutputTypes =
            new Dictionary<string, string>
            {
                ["script"] = "unity.AssetRef<unity.MonoScript>",
            };

        private static CreateScriptOperation ScriptOp() =>
            new CreateScriptOperation(() => new[] { "Assets/" });

        private static UnityStepContext ScriptContext(string path)
        {
            var inputs = new Dictionary<string, InputValue>
            {
                ["path"] = InputValue.Present(Json("\"" + path + "\"")),
                ["content"] = InputValue.Present(
                    JsonValue.From(JToken.FromObject("// probe\n"))),
            };
            return new UnityStepContext(
                inputs,
                ScriptInputTypes,
                ScriptOutputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "script"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)));
        }

        // unity.script.create's Preflight refuses the write against a
        // protected assembly — OW-RUN-013 in the diagnostic set, no
        // snapshot produced.
        [Test]
        public void ScriptCreatePreflightRefusesAProtectedAssembly()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.ScriptAssembly["Assets/GuardProbe.cs"] =
                "Protected.Ops";

            var preflight = ScriptOp().Preflight(ScriptContext("Assets/GuardProbe.cs"));

            Assert.That(preflight.Diagnostics.Select(d => d.Code),
                Contains.Item(ErrorCodes.OW_RUN_013));
        }

        // A Run accepted between Preflight and Execute is caught by the
        // re-check — the step fails OW-RUN-013 and writes nothing
        // (SPEC §D0.3 item 4's serialization guarantee, main thread).
        [Test]
        public void ScriptCreateExecuteRefusesARunAcceptedAfterPreflight()
        {
            var harness = NewHarness();
            harness.Queries.ScriptAssembly["Assets/GuardProbe.cs"] =
                "Protected.Ops";
            var path = "Assets/GuardProbe.cs";
            var physical = Path.Combine(
                RunnerHostFixture.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var preflight = ScriptOp().Preflight(ScriptContext(path));
                Assert.That(preflight.Diagnostics, Is.Empty,
                    "with no unfinished Run the write clears Preflight");

                AddProtectedRun(harness, "run_late");

                var execute = ScriptOp().Execute(ScriptContext(path));
                Assert.That(execute.Kind, Is.EqualTo(StepResultKind.Failed));
                Assert.That(execute.Error!.Code, Is.EqualTo(ErrorCodes.OW_RUN_013));
                Assert.IsFalse(File.Exists(physical),
                    "a refused step must not write");
            }
            finally
            {
                if (File.Exists(physical))
                {
                    try { File.Delete(physical); } catch (Exception) { }
                    try { File.Delete(physical + ".meta"); } catch (Exception) { }
                    AssetDatabase.Refresh();
                }
            }
        }

        // The same recheck admits the write once every Run is terminal —
        // terminal Runs protect nothing (SPEC §D0.3 item 1).
        [Test]
        public void ScriptCreateExecuteProceedsWhenNoRunsAreProtected()
        {
            var harness = NewHarness();
            harness.Queries.ScriptAssembly["Assets/GuardProbe.cs"] =
                "Protected.Ops";
            // The unfinished Runs list is empty — a terminal Run protects
            // nothing, so the same path passes both checks.
            var path = "Assets/GuardProbe.cs";
            var physical = Path.Combine(
                RunnerHostFixture.ProjectRoot, path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var execute = ScriptOp().Execute(ScriptContext(path));
                Assert.That(execute.Kind, Is.EqualTo(StepResultKind.Succeeded),
                    () => execute.Error is { } e
                        ? e.Code + " " + e.Message
                        : "no error");
                Assert.IsTrue(File.Exists(physical));
            }
            finally
            {
                if (File.Exists(physical))
                {
                    try { File.Delete(physical); } catch (Exception) { }
                    try { File.Delete(physical + ".meta"); } catch (Exception) { }
                    AssetDatabase.Refresh();
                }
            }
        }

        // ── Diagnostic hygiene (SPEC §D0.3 / §J) ────────────────────────

        // The refusal diagnostic reports only the normalized target and Run
        // ids — never source text or URLs.
        [Test]
        public void RefusalDiagnosticsCarryOnlySafeIdentifiers()
        {
            var harness = NewHarness();
            AddProtectedRun(harness, "run_a");
            harness.Queries.ScriptAssembly["Assets/Thing.cs"] = "Protected.Ops";

            var diagnostic = RunDefinitionGuard.CheckPath(
                "Assets/Thing.cs", GuardedPathRole.Destination);

            AssertRefused(diagnostic);
            StringAssert.DoesNotContain("://", diagnostic!.Message);
            StringAssert.DoesNotContain("://", diagnostic.Hint ?? "");
            StringAssert.Contains("Assets/Thing.cs", diagnostic.Hint);
        }
    }
}
