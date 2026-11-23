#nullable enable

// T-W3.5-03 [Edit] tests: local:T-W3.5-03:registry-tab — SPEC §H5 item 2.
// The Registry tab lists registered Operations with origin filtering and
// openable custom sources, shows every OW-OP-013..019 failure/reason and
// unrelated valid RegistrySnapshot entries without exposing passwords or
// tokens, lists registered extensions, exclusions with reasons, the stale
// flag, settings errors and the diff since the previous Registry (or its
// explicit unavailable state), offers no control that can admit a
// whitelist-excluded extension, and shows "Setup Project" only while the
// SPEC §M.3 setup is incomplete. The [UnityTest] crosses real domain
// reloads: adding and removing a custom Operation file under Assets/
// lands in the persisted diff rows, and setup visibility toggles on the
// file-backed §M.3 state.
//
// The detached-tree fixtures populate a CreateInstance'd window through
// the same BuildView path OnEnable uses and drive the same internal entry
// points the callbacks call; every build runs the real
// RegistryBuildHost.Rebuild pipeline over injected discovery seams, and
// the diff tests persist through a real RegistryDiffRecorder rooted in a
// scratch Library directory.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using OpWeave.Viewer;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RegistryViewerTabTests
    {
        // ── Fixture identities ──────────────────────────────────────────

        private const string AlphaOpId = "test.regtab.alpha";
        private const string BetaOpId = "test.regtab.beta";
        private const string SecretMarker = "sk-secret-9f3b-regtab";

        private const string ProbeOpId = "test.regtab.probe";
        private const string ProbeAssetDir = "Assets/OpWeaveRegistryTabProbe";
        private const string ProbeAssembly = "OpWeave.RegistryTabProbe";
        private const string ProbeFileName = "TabProbeOp.cs";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // The [UnityTest] needs paths that survive a domain reload, so the
        // scratch roots are static and constant rather than per-test
        // instance state; [Test] fixtures get a fresh directory per run.
        private static string UnityScratchRoot =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveRegistryTabProbe");

        private static string UnityStateDir => Path.Combine(UnityScratchRoot, "state");
        private static string UnitySetupRoot => Path.Combine(UnityScratchRoot, "project");
        private static string ProbeDiskDir => Path.Combine(ProjectRoot, ProbeAssetDir);
        private static string ProbeFile => Path.Combine(ProbeDiskDir, ProbeFileName);

        private string _stateDir = null!;
        private string _setupRoot = null!;
        private readonly List<EditorWindow> _windows = new List<EditorWindow>();

        [SetUp]
        public void SetUp()
        {
            var root = Path.Combine(
                ProjectRoot, "Library", "OpWeaveRegistryTabTests",
                "t" + Guid.NewGuid().ToString("N"));
            _stateDir = Path.Combine(root, "state");
            _setupRoot = Path.Combine(root, "project");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var window in _windows)
            {
                if (window != null)
                {
                    UnityEngine.Object.DestroyImmediate(window);
                }
            }
            foreach (var dir in new[] { _stateDir, _setupRoot })
            {
                var root = Directory.GetParent(dir)?.FullName;
                if (root is not null && Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        // ── Bound window fixture ────────────────────────────────────────
        //
        // A detached-tree window bound to an isolated publication, a
        // scratch recorder root and a scratch setup root — the production
        // seams are the same RegistryViewerSource.Production wires, only
        // pointed at test state.

        private sealed class Bound
        {
            internal readonly RunViewerWindow Window;
            internal readonly RegistryPublication Publication = new RegistryPublication();
            internal readonly RunEvents Dispatcher = new RunEvents();
            internal RegistryBuildResult? Build;
            internal RegistryDiffRecorder? Recorder;
            internal readonly string StateDir;
            internal readonly string SetupRoot;
            internal readonly List<string> Opened = new List<string>();
            internal readonly List<OpWeaveSetupResult> Logged =
                new List<OpWeaveSetupResult>();

            internal Bound(RunViewerWindow window, string stateDir, string setupRoot)
            {
                Window = window;
                StateDir = stateDir;
                SetupRoot = setupRoot;
            }

            internal void Bind(Func<RegistryBuildResult?>? latestBuild = null) =>
                Window.BindRegistrySource(new RegistryViewerSource(
                    Publication,
                    latestBuild ?? (() => Build),
                    () => Recorder?.Latest,
                    () => Dispatcher,
                    () => OpWeaveProjectSetup.IsSetupComplete(SetupRoot),
                    () => OpWeaveProjectSetup.Run(SetupRoot),
                    result => Logged.Add(result),
                    path => { Opened.Add(path!); return true; }));

            internal RegistryDiffRecorder NewRecorder() =>
                Recorder = new RegistryDiffRecorder(
                    StateDir, () => Dispatcher, _ => { }, _ => { });

            internal ScrollView Pane =>
                Window.rootVisualElement.Q<ScrollView>(RegistryViewerTab.PaneName);

            internal List<string> Texts(string name) =>
                Window.rootVisualElement.Query<Label>(name).ToList()
                    .Select(label => label.text).ToList();

            internal string Joined(string name) => string.Join("\n", Texts(name));
        }

        private Bound NewBound(
            string? stateDir = null, string? setupRoot = null,
            Func<RegistryBuildResult?>? latestBuild = null)
        {
            var window = ScriptableObject.CreateInstance<RunViewerWindow>();
            _windows.Add(window);
            window.BuildView();
            var bound = new Bound(
                window, stateDir ?? _stateDir, setupRoot ?? _setupRoot);
            bound.Bind(latestBuild);
            return bound;
        }

        // ── Discovery/build seams ───────────────────────────────────────

        private static OperationDescriptor Describe(string id, int version) =>
            Op.Define(id, version)
                .Summary("A Registry tab fixture operation.")
                .Output("done", "core.Bool")
                .Effects(EffectNames.Read)
                .Idempotency(Idempotency.Idempotent)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(id, version, outputPort: "done"),
                    },
                };

        internal abstract class RegTabOperation : IOperation
        {
            public abstract OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(),
                    JsonValue.Parse("{}").Value!);

            public StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("done", true);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException("Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                new ReconcileResult.Unknown("This fixture never reconciles.");

            public void Cancel(StepContext ctx)
            {
            }
        }

        [OpWeaveOperation(AlphaOpId, 1)]
        internal sealed class AlphaOperation : RegTabOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Describe(AlphaOpId, 1);
        }

        [OpWeaveOperation(BetaOpId, 1)]
        internal sealed class BetaOperation : RegTabOperation
        {
            public override OperationDescriptor Descriptor { get; } =
                Describe(BetaOpId, 1);
        }

        // The §E8 refresh test's baseline-only extension: the second build
        // never scans this class, so its row proves the event-driven
        // refresh replaced the extension rows rather than keeping the
        // previous build's set.
        [OpWeaveTypeProvider]
        internal sealed class BaselineOnlyProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => new[]
            {
                new TypeDefinition("test.regtab.baseline", TypeKind.Primitive, 0,
                    Array.Empty<string>(), null, "Baseline-only fixture type.",
                    StringCodec.Instance),
            };
        }

        // Re-attributes the listed types as non-standard (project assembly)
        // provenance — the same re-attribution the discovery-mode tests
        // use, since this test assembly itself lives in the standard
        // package.
        private static Func<Type, Result<EditorAssemblyProvenance>> CustomProvenance(
            params Type[] customTypes) =>
            type =>
            {
                var resolved = EditorAssemblyResolver.ResolveForType(type);
                if (resolved.Ok && customTypes.Contains(type))
                {
                    var real = resolved.Value!;
                    resolved = Result<EditorAssemblyProvenance>.Success(
                        new EditorAssemblyProvenance(
                            real.AssemblyName, real.CompiledAssemblyPath,
                            real.IsPrecompiled, OperationOrigin.Custom,
                            package: null, real.CompilationAssembly));
                }
                return resolved;
            };

        private static RegistryTypeStage.Seams FixtureSeams(
            IExtensionExclusionPredicate exclusion,
            IEnumerable<Type> operationTypes,
            Func<Type, Result<EditorAssemblyProvenance>>? provenance = null) =>
            RegistryTypeStage.Production(exclusion) with
            {
                OperationTypes = () => operationTypes,
                ProviderTypes = () => Array.Empty<Type>(),
                ResolveProvenance = provenance ??
                    (type => EditorAssemblyResolver.ResolveForType(type)),
            };

        private static RegistryBuildResult Rebuild(
            RegistryTypeStage.Seams seams,
            RegistryPublication publication,
            IReadOnlyList<Diagnostic>? settingsDiagnostics = null,
            RegistryDiffRecorder? recorder = null) =>
            RegistryBuildHost.Rebuild(
                seams, RegistryOperationStage.Production(), publication,
                settingsDiagnostics, recorder);

        private static RegistryBuildResult RebuildProduction(
            RegistryPublication publication, RegistryDiffRecorder? recorder = null) =>
            RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(), publication,
                diffRecorder: recorder);

        // The diagnostics fixtures of T-W2.12-03, reused so the tab shows
        // the real pipeline's OW-OP-013..017/019 records rather than
        // synthetic ones; OW-OP-018 comes from the production build below
        // (com.opweave.fixture.mismatch's declared-vs-observed verdict).
        private static readonly Type[] s_diagProviders =
        {
            typeof(RegistrationDiagnosticsTests.DiagCollideAProvider),
            typeof(RegistrationDiagnosticsTests.DiagCollideBProvider),
            typeof(RegistrationDiagnosticsTests.DiagBaselessProvider),
            typeof(RegistrationDiagnosticsTests.DiagCycleProvider),
            typeof(RegistrationDiagnosticsTests.DiagNoCodecProvider),
            typeof(RegistrationDiagnosticsTests.DiagBadNormalizeProvider),
            typeof(RegistrationDiagnosticsTests.DiagThrowCtorProvider),
            typeof(RegistrationDiagnosticsTests.DiagUnresolvableProvider),
            typeof(RegistrationDiagnosticsTests.DiagMalformedIdProvider),
            typeof(RegistrationDiagnosticsTests.DiagThrowGetTypesProvider),
            typeof(RegistrationDiagnosticsTests.DiagNullGetTypesProvider),
            typeof(RegistrationDiagnosticsTests.DiagNullDefProvider),
            typeof(RegistrationDiagnosticsTests.DiagGoodProvider),
        };

        private static RegistryTypeStage.Seams DiagSeams() =>
            new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => false,
                OperationTypes: () => new[]
                {
                    typeof(RegistrationDiagnosticsTests.DiagGoodOperation),
                    typeof(RegistrationDiagnosticsTests.DiagDependentOperation),
                },
                ProviderTypes: () => s_diagProviders,
                ResolveProvenance: type =>
                    type == typeof(RegistrationDiagnosticsTests.DiagUnresolvableProvider)
                        ? Result<EditorAssemblyProvenance>.Failure(
                            ErrorCodes.Create(
                                ErrorCodes.OW_OP_017,
                                "The defining assembly could not be resolved as Editor-only."))
                        : EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());

        private static OpWeaveSettings WhitelistSettings()
        {
            var parsed = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"extensions\":{\"discoveryMode\":\"whitelist\"," +
                "\"integrityLevel\":\"weak\",\"whitelist\":{\"operations\":[]," +
                "\"typeProviders\":[]}}}");
            Assert.That(parsed.Ok, Is.True, "the settings fixture must parse");
            return parsed.Value!;
        }

        // ── Window construction (#384 F1) ─────────────────────────────

        [Test]
        public void Window_BuildsRegistryPane_ThroughBuildView_NeverInConstructor()
        {
            // #384 F1: the Registry tab creates UI Toolkit elements, so it
            // must not exist at ScriptableObject construction time — Unity
            // rejects VisualElement creation there, and the unhandled
            // exception then failed every viewer fixture. CreateInstance
            // plus BuildView (the same path OnEnable runs) must leave a
            // queryable Registry pane.
            var bound = NewBound();
            Assert.That(bound.Pane, Is.Not.Null,
                "the Registry pane must exist after BuildView");
            Assert.That(bound.Pane.Q(RegistryViewerTab.SetupButtonName),
                Is.Not.Null,
                "the pane must carry the tab's constructed rows");
        }

        // ── Registered Operations: origin filter, provenance, open ──────

        [Test]
        public void Operations_ListedWithOrigin_AndCustomSourceOpens()
        {
            var bound = NewBound();
            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance,
                    new[] { typeof(AlphaOperation), typeof(BetaOperation) },
                    CustomProvenance(typeof(BetaOperation))),
                bound.Publication);
            bound.Window.SelectRegistryTab();

            var titles = bound.Texts(RegistryViewerTab.OperationTitleName);
            Assert.That(
                titles.Any(t => t.Contains(AlphaOpId) && t.Contains("standard")),
                Is.True, "the standard Operation must be listed as standard");
            Assert.That(
                titles.Any(t => t.Contains(BetaOpId) && t.Contains("custom")),
                Is.True, "the re-attributed Operation must be listed as custom");
            var details = bound.Texts(RegistryViewerTab.OperationDetailName);
            Assert.That(details.Any(t => t.Contains("Assembly-CSharp") ||
                t.Contains("OpWeave.UnityAdapter.Tests")), Is.True,
                "rows carry the defining assembly");

            // Custom sources are openable; standard rows are not offered the
            // path (SPEC §H5: カスタムはソースを開ける).
            var beta = bound.Build.Snapshot!.Find(BetaOpId, 1)!;
            Assert.That(beta.Origin, Is.EqualTo(OperationOrigin.Custom));
            Assert.That(beta.SourceFile, Is.Not.Null);
            bound.Window.ActivateRegistrySource(beta);
            Assert.That(bound.Opened, Has.Count.EqualTo(1));
            Assert.That(bound.Opened[0], Is.EqualTo(beta.SourceFile));

            var alpha = bound.Build.Snapshot!.Find(AlphaOpId, 1)!;
            bound.Window.ActivateRegistrySource(alpha);
            Assert.That(bound.Opened, Has.Count.EqualTo(1),
                "a standard-origin row must not open a source through the tab");
        }

        [Test]
        public void OriginFilter_NarrowsListedOperations()
        {
            var bound = NewBound();
            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance,
                    new[] { typeof(AlphaOperation), typeof(BetaOperation) },
                    CustomProvenance(typeof(BetaOperation))),
                bound.Publication);
            bound.Window.SelectRegistryTab();

            bound.Window.SetRegistryOriginFilter(RegistryViewerTab.OriginFilterCustom);
            var titles = bound.Texts(RegistryViewerTab.OperationTitleName);
            Assert.That(titles, Has.Count.EqualTo(1));
            Assert.That(titles[0], Does.Contain(BetaOpId));

            bound.Window.SetRegistryOriginFilter(RegistryViewerTab.OriginFilterStandard);
            titles = bound.Texts(RegistryViewerTab.OperationTitleName);
            Assert.That(titles, Has.Count.EqualTo(1));
            Assert.That(titles[0], Does.Contain(AlphaOpId));

            bound.Window.SetRegistryOriginFilter(RegistryViewerTab.OriginFilterAll);
            titles = bound.Texts(RegistryViewerTab.OperationTitleName);
            Assert.That(titles, Has.Count.EqualTo(2));
        }

        // ── Failures: OW-OP-013..019 and no secrets ─────────────────────

        [Test]
        public void Failures_DisplaysOwOp013Through017And019_AndValidEntries_WithoutSecrets()
        {
            var bound = NewBound();
            bound.Build = Rebuild(DiagSeams(), bound.Publication);
            bound.Window.SelectRegistryTab();

            var failureText = bound.Joined(RegistryViewerTab.FailureLineName);
            foreach (var code in new[]
            {
                ErrorCodes.OW_OP_013, ErrorCodes.OW_OP_014, ErrorCodes.OW_OP_015,
                ErrorCodes.OW_OP_016, ErrorCodes.OW_OP_017, ErrorCodes.OW_OP_019,
            })
            {
                StringAssert.Contains(code, failureText,
                    "the tab must surface " + code);
            }

            // Unrelated valid entries still list: the healthy fixture
            // Operation and the colliding providers' rejection reasons.
            Assert.That(bound.Texts(RegistryViewerTab.OperationTitleName)
                .Any(t => t.Contains("test.diag.good.op")), Is.True);
            Assert.That(bound.Texts(RegistryViewerTab.FailureTitleName)
                .Any(t => t.Contains("test.diag.shared")), Is.True);

            // No credential-shaped text reaches the pane (the fixtures
            // embed a secret marker in their exception text).
            foreach (var label in bound.Pane.Query<Label>().ToList())
            {
                Assert.That(label.text, Does.Not.Contain("sk-secret"));
            }
        }

        [Test]
        public void Failures_DisplaysOwOp018_FromTheExtensionGate()
        {
            var bound = NewBound();
            // The real discovery: com.opweave.fixture.mismatch's declared
            // operations[] contradict its assemblies — OW-OP-018 — while
            // the valid extension registers unaffected.
            bound.Build = RebuildProduction(bound.Publication);
            bound.Window.SelectRegistryTab();

            Assert.That(bound.Build.Snapshot!.CandidateFailures
                .Any(f => f.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_018)),
                Is.True, "the fixture mismatch package must produce OW-OP-018");
            var failureText = bound.Joined(RegistryViewerTab.FailureLineName);
            StringAssert.Contains(ErrorCodes.OW_OP_018, failureText);
            Assert.That(bound.Texts(RegistryViewerTab.OperationTitleName)
                .Any(t => t.Contains("com.opweave.fixture.valid.op") &&
                    t.Contains("custom")), Is.True,
                "the unrelated valid extension stays listed");
        }

        // ── Exclusions: listed with reasons, no enable control ──────────

        [Test]
        public void ExcludedCandidate_ListedAsExclusion_NeverRegistered_NoEnableControl()
        {
            var bound = NewBound();
            bound.Build = Rebuild(
                FixtureSeams(
                    SettingsExtensionExclusion.For(WhitelistSettings()),
                    new[] { typeof(AlphaOperation), typeof(BetaOperation) },
                    CustomProvenance(typeof(BetaOperation))),
                bound.Publication);
            bound.Window.SelectRegistryTab();

            // The whitelist-excluded custom candidate is Registry state —
            // an exclusion row with its reason, never a registration row.
            Assert.That(bound.Build.Snapshot!.Find(BetaOpId, 1), Is.Null);
            var exclusions = bound.Texts(RegistryViewerTab.ExclusionLineName);
            var line = exclusions.FirstOrDefault(t => t.Contains(BetaOpId));
            Assert.That(line, Is.Not.Null,
                "the excluded custom Operation must appear in the exclusions");
            StringAssert.Contains(
                SettingsExtensionExclusion.OperationWhitelistReason, line!);
            var titles = bound.Texts(RegistryViewerTab.OperationTitleName);
            Assert.That(titles, Has.Count.EqualTo(1));
            Assert.That(titles[0], Does.Contain(AlphaOpId),
                "the standard Operation stays admitted under whitelist mode");

            // No control can register the excluded candidate: the only
            // Button inside the Registry pane is the Setup Project entry —
            // exclusions are rows, not actions (SPEC §C3 rule 5).
            foreach (var button in bound.Pane.Query<Button>().ToList())
            {
                Assert.That(button.name,
                    Is.EqualTo(RegistryViewerTab.SetupButtonName),
                    "no pane button may register or enable an extension");
            }
        }

        // ── Stale and settings diagnostics ──────────────────────────────

        [Test]
        public void Stale_AndNoSnapshot_ShownExplicitly()
        {
            var bound = NewBound();
            bound.Window.SelectRegistryTab();
            Assert.That(bound.Window.rootVisualElement
                .Q<Label>(RegistryViewerTab.StatusName)!.text,
                Does.Contain("No Registry published yet"));
            var stale = bound.Window.rootVisualElement
                .Q<Label>(RegistryViewerTab.StaleName)!;
            Assert.That(stale.style.display.value, Is.EqualTo(DisplayStyle.Flex),
                "a holder starts stale before its first build");

            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance, new[] { typeof(AlphaOperation) }),
                bound.Publication);
            bound.Window.RefreshRegistry();
            Assert.That(stale.style.display.value, Is.EqualTo(DisplayStyle.None));

            bound.Publication.MarkStale();
            bound.Window.RefreshRegistry();
            Assert.That(stale.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(bound.Texts(RegistryViewerTab.OperationTitleName)
                .Any(t => t.Contains(AlphaOpId)), Is.True,
                "the stale marker keeps the last published snapshot visible");
        }

        [Test]
        public void SettingsDiagnostics_Listed()
        {
            var bound = NewBound();
            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance, new[] { typeof(AlphaOperation) }),
                bound.Publication,
                settingsDiagnostics: new[]
                {
                    ErrorCodes.Create(
                        "OW-CFG-001", "the settings file could not be parsed"),
                });
            bound.Window.SelectRegistryTab();
            var text = bound.Joined(RegistryViewerTab.SettingsLineName);
            StringAssert.Contains("OW-CFG-001", text);
            StringAssert.Contains("could not be parsed", text);
        }

        // ── Registered extensions ───────────────────────────────────────

        [Test]
        public void RegisteredExtensions_ProvidersListed()
        {
            var bound = NewBound();
            bound.Build = Rebuild(DiagSeams(), bound.Publication);
            bound.Window.SelectRegistryTab();
            var lines = bound.Texts(RegistryViewerTab.ExtensionLineName);
            Assert.That(lines.Any(t => t.StartsWith("provider ") &&
                t.Contains("DiagGoodProvider")), Is.True,
                "the instantiated provider must be listed");

            var packaged = NewBound();
            packaged.Build = RebuildProduction(packaged.Publication);
            packaged.Window.SelectRegistryTab();
            Assert.That(packaged.Texts(RegistryViewerTab.ExtensionLineName)
                .Any(t => t == "package com.opweave.fixture.valid"), Is.True,
                "the admitted extension package must be listed");
        }

        // ── Diff rows ───────────────────────────────────────────────────

        [Test]
        public void Diff_UnavailablePrevious_ThenAddedAndRemoved()
        {
            var bound = NewBound();
            var recorder = bound.NewRecorder();

            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance, new[] { typeof(AlphaOperation) }),
                bound.Publication, recorder: recorder);
            bound.Window.SelectRegistryTab();
            StringAssert.Contains("previous snapshot unavailable",
                bound.Window.rootVisualElement
                    .Q<Label>(RegistryViewerTab.DiffStatusName)!.text);

            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance,
                    new[] { typeof(AlphaOperation), typeof(BetaOperation) },
                    CustomProvenance(typeof(BetaOperation))),
                bound.Publication, recorder: recorder);
            bound.Window.RefreshRegistry();
            Assert.That(bound.Texts(RegistryViewerTab.DiffLineName)
                .Any(t => t.Contains("added") && t.Contains(BetaOpId)),
                Is.True, "adding a custom Operation must appear in the diff");

            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance, new[] { typeof(AlphaOperation) }),
                bound.Publication, recorder: recorder);
            bound.Window.RefreshRegistry();
            Assert.That(bound.Texts(RegistryViewerTab.DiffLineName)
                .Any(t => t.Contains("removed") && t.Contains(BetaOpId)),
                Is.True, "removing a custom Operation must appear in the diff");
        }

        // ── SPEC §E8 refresh ────────────────────────────────────────────

        [Test]
        public void RegistryChanged_Event_RefreshesPane()
        {
            var bound = NewBound();
            var recorder = bound.NewRecorder();
            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance, new[] { typeof(AlphaOperation) }),
                bound.Publication, recorder: recorder);
            bound.Window.SelectRegistryTab();
            Assert.That(bound.Texts(RegistryViewerTab.OperationTitleName),
                Has.Count.EqualTo(1));

            // The recorder's dispatch — record-before-publish (SPEC §E8) —
            // repaints the pane with no manual refresh.
            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance,
                    new[] { typeof(AlphaOperation), typeof(BetaOperation) },
                    CustomProvenance(typeof(BetaOperation))),
                bound.Publication, recorder: recorder);
            var titles = bound.Texts(RegistryViewerTab.OperationTitleName);
            Assert.That(titles, Has.Count.EqualTo(2),
                "RegistryChanged must repaint the pane");
            Assert.That(bound.Texts(RegistryViewerTab.DiffLineName)
                .Any(t => t.Contains("added") && t.Contains(BetaOpId)),
                Is.True);
        }

        [Test]
        public void RegistryChanged_Event_RefreshesSettingsAndExtensionRows()
        {
            // #384 F2: bound over the domain's retained build — the
            // production RegistryViewerSource wire — so the event-driven
            // refresh reads whatever RegistryBuildHost.Latest held when
            // the recorder dispatched RegistryChanged mid-Rebuild.
            var bound = NewBound(latestBuild: () => RegistryBuildHost.Latest);
            var recorder = bound.NewRecorder();

            // Baseline: a settings diagnostic and a baseline-only
            // provider, so every build-backed row must differ after the
            // second build. The standard providers are wired into every
            // build (SPEC §0.6 SG-10), so the extension section lists them
            // even when nothing was discovered.
            Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance,
                    new[] { typeof(AlphaOperation) }) with
                {
                    ProviderTypes = () => new[] { typeof(BaselineOnlyProvider) },
                },
                bound.Publication,
                settingsDiagnostics: new[]
                {
                    ErrorCodes.Create("OW-CFG-001", "baseline settings error"),
                },
                recorder: recorder);
            bound.Window.SelectRegistryTab();
            StringAssert.Contains("OW-CFG-001",
                bound.Joined(RegistryViewerTab.SettingsLineName));
            var baselineExtensions =
                bound.Joined(RegistryViewerTab.ExtensionLineName);
            StringAssert.Contains("provider @builtin", baselineExtensions);
            StringAssert.Contains("provider opweave.unity-types",
                baselineExtensions);
            StringAssert.Contains("BaselineOnlyProvider", baselineExtensions);

            // The second build registers different Operations — the
            // recorded batch is non-empty, so the recorder dispatches
            // RegistryChanged inside Rebuild — and it carries different
            // settings diagnostics plus a different provider set. With the
            // Registry tab still selected and no manual refresh, the pane
            // must already show THIS build's rows, never the baseline's.
            var second = Rebuild(
                DiagSeams(),
                bound.Publication,
                settingsDiagnostics: new[]
                {
                    ErrorCodes.Create("OW-CFG-002", "rebuilt settings error"),
                },
                recorder: recorder);

            var settingsLines = bound.Texts(RegistryViewerTab.SettingsLineName);
            Assert.That(settingsLines,
                Has.Count.EqualTo(second.SettingsDiagnostics.Count),
                "the pane must list exactly this build's settings rows");
            var settings = bound.Joined(RegistryViewerTab.SettingsLineName);
            StringAssert.Contains("OW-CFG-002", settings);
            Assert.That(settings, Does.Not.Contain("OW-CFG-001"),
                "the previous build's settings diagnostics must not linger");

            var extensionLines =
                bound.Texts(RegistryViewerTab.ExtensionLineName);
            Assert.That(extensionLines,
                Has.Count.EqualTo(second.Providers.Count +
                    second.TypeStage.ExtensionPackages.Count),
                "the pane must list exactly this build's extension rows");
            Assert.That(extensionLines
                .Any(t => t.Contains("BaselineOnlyProvider")), Is.False,
                "the previous build's provider rows must not linger");
            Assert.That(extensionLines
                .Any(t => t.StartsWith("provider ") &&
                    t.Contains("DiagGoodProvider")), Is.True,
                "the event refresh must list the new build's providers");
        }

        // ── Setup Project ───────────────────────────────────────────────

        [Test]
        public void SetupButton_ShownOnlyBeforeSetup_RunsAndHides()
        {
            var bound = NewBound();
            bound.Build = Rebuild(
                FixtureSeams(
                    AutoExtensionExclusion.Instance, new[] { typeof(AlphaOperation) }),
                bound.Publication);
            bound.Window.SelectRegistryTab();

            var panel = bound.Pane.Q(RegistryViewerTab.SetupPanelName);
            Assert.That(panel, Is.Not.Null);
            Assert.That(panel.style.display.value, Is.EqualTo(DisplayStyle.Flex),
                "Setup Project must be offered while §M.3 setup is incomplete");
            Assert.That(OpWeaveProjectSetup.IsSetupComplete(bound.SetupRoot),
                Is.False);

            bound.Window.InvokeRegistrySetup();

            Assert.That(bound.Logged, Has.Count.EqualTo(1),
                "the run's result must be listed through the seam");
            Assert.That(OpWeaveProjectSetup.IsSetupComplete(bound.SetupRoot),
                Is.True);
            Assert.That(panel.style.display.value, Is.EqualTo(DisplayStyle.None),
                "the button hides once setup completed");
        }

        // ── Domain-reload [UnityTest]: add/remove + setup toggle ────────

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        private const string ProbeAsmdef =
            "{\n    \"name\": \"" + ProbeAssembly + "\",\n" +
            "    \"rootNamespace\": \"OpWeave.RegistryTabProbeNs\",\n" +
            "    \"references\": [\"OpWeave.Core\"],\n" +
            "    \"includePlatforms\": [\"Editor\"],\n" +
            "    \"autoReferenced\": false\n}\n";

        private const string ProbeSource =
            "#nullable enable\n" +
            "using System;\n" +
            "using System.Collections.Generic;\n" +
            "using OpWeave.Core;\n" +
            "using OpWeave.Core.Operations;\n\n" +
            "namespace OpWeave.RegistryTabProbeNs\n{\n" +
            "    [OpWeaveOperation(\"" + ProbeOpId + "\", 1)]\n" +
            "    public sealed class TabProbeOperation : IOperation\n" +
            "    {\n" +
            "        public OperationDescriptor Descriptor { get; } = Op.Define(\"" + ProbeOpId + "\", 1)\n" +
            "            .Summary(\"Registry tab probe operation.\")\n" +
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
            "            ctx.SetOutput(\"done\", true);\n" +
            "            return StepResult.Succeeded();\n" +
            "        }\n" +
            "        public StepResult Poll(StepContext ctx) =>\n" +
            "            throw new NotSupportedException(\"Immediate operations are never polled.\");\n" +
            "        public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>\n" +
            "            new ReconcileResult.Unknown(\"This probe never reconciles.\");\n" +
            "        public void Cancel(StepContext ctx) { }\n" +
            "    }\n" +
            "}\n";

        private static void WriteProbe()
        {
            Directory.CreateDirectory(ProbeDiskDir);
            File.WriteAllText(
                Path.Combine(ProbeDiskDir, ProbeAssembly + ".asmdef"), ProbeAsmdef);
            File.WriteAllText(ProbeFile, ProbeSource);
            AssetDatabase.Refresh();
        }

        private static void DeleteProbe()
        {
            File.Delete(ProbeFile);
            File.Delete(ProbeFile + ".meta");
            AssetDatabase.Refresh();
        }

        private static bool CleanupProbe()
        {
            var dirty = false;
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
            if (Directory.Exists(UnityScratchRoot))
            {
                Directory.Delete(UnityScratchRoot, recursive: true);
            }
            return dirty;
        }

        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            if (CleanupProbe())
            {
                yield return new RecompileScripts(false);
            }
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            if (CleanupProbe())
            {
                yield return new RecompileScripts(false);
            }
            if (!EditorUtility.scriptCompilationFailed && !EditorApplication.isCompiling)
            {
                RegistryBuildHost.RebuildProduction();
            }
        }

        // Every phase asserts inside its own domain — only files cross the
        // reloads (SPEC §T.3). The same scratch recorder root persists the
        // registry.json the build diffs against, and the scratch setup root
        // persists the §M.3 completion state the button visibility reads.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator CustomAddRemove_ShowsInDiff_AndSetupTogglesAcrossReload()
        {
            // Baseline without the probe: the persisted registry.json the
            // next build diffs against.
            var bound = NewBound(UnityStateDir, UnitySetupRoot);
            var recorder = bound.NewRecorder();
            bound.Build = RebuildProduction(bound.Publication, recorder);
            bound.Window.SelectRegistryTab();
            Assert.That(bound.Pane.Q(RegistryViewerTab.SetupPanelName)!
                    .style.display.value, Is.EqualTo(DisplayStyle.Flex),
                "Setup Project must be offered before setup");
            Assert.That(bound.Window.rootVisualElement
                .Q<Label>(RegistryViewerTab.DiffStatusName)!.text,
                Does.Contain("unavailable"));

            // Add the probe file and cross the reload.
            WriteProbe();
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the probe compile did not produce a domain reload");

            var added = NewBound(UnityStateDir, UnitySetupRoot);
            var addedRecorder = added.NewRecorder();
            added.Build = RebuildProduction(added.Publication, addedRecorder);
            added.Window.SelectRegistryTab();
            Assert.That(added.Texts(RegistryViewerTab.OperationTitleName)
                .Any(t => t.Contains(ProbeOpId) && t.Contains("custom")), Is.True,
                "the added custom Operation must be listed as custom");
            Assert.That(added.Texts(RegistryViewerTab.DiffLineName)
                .Any(t => t.Contains("added") && t.Contains(ProbeOpId)),
                Is.True, "adding a custom Operation must appear in the diff");

            // Setup completes on disk; the button hides in the same domain.
            Assert.That(added.Pane.Q(RegistryViewerTab.SetupPanelName)!
                    .style.display.value, Is.EqualTo(DisplayStyle.Flex));
            added.Window.InvokeRegistrySetup();
            Assert.That(OpWeaveProjectSetup.IsSetupComplete(UnitySetupRoot),
                Is.True);
            Assert.That(added.Pane.Q(RegistryViewerTab.SetupPanelName)!
                    .style.display.value, Is.EqualTo(DisplayStyle.None));

            // Remove the probe file and cross the reload.
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

            var removed = NewBound(UnityStateDir, UnitySetupRoot);
            var removedRecorder = removed.NewRecorder();
            removed.Build = RebuildProduction(removed.Publication, removedRecorder);
            removed.Window.SelectRegistryTab();
            Assert.That(removed.Texts(RegistryViewerTab.OperationTitleName)
                .Any(t => t.Contains(ProbeOpId)), Is.False,
                "the removed Operation must leave the list");
            Assert.That(removed.Texts(RegistryViewerTab.DiffLineName)
                .Any(t => t.Contains("removed") && t.Contains(ProbeOpId)),
                Is.True, "removing a custom Operation must appear in the diff");
            Assert.That(removed.Pane.Q(RegistryViewerTab.SetupPanelName)!
                    .style.display.value, Is.EqualTo(DisplayStyle.None),
                "the file-backed §M.3 state survives the domain reload");
        }
    }
}
