#nullable enable
// T-W3.7-02 [Edit] tests: local:T-W3.7-02:at04-no-pipeline — SPEC §T.3
// AT-04 (W3.7-R04) and the §H2 test-4 half (W3.7-R05): com.unity.pipeline
// is an optional adapter — remove it and the Core, Operations and
// custom-Operation suites plus the structural tests still pass, and
// OpWeave.Pipeline is not compiled.
//
// This file lives in OpWeave.UnityAdapter.Tests — the assembly that
// compiles with or without the package — so the same fixture runs in
// both configurations. It reads the live configuration from three
// independent sources — the manifest.json "com.unity.pipeline"
// declaration, the PackageInfo-resolved package set and the compiled
// OpWeave.Pipeline assembly — and requires all three to agree: a
// declared-but-unresolved or resolved-but-uncompiled half-state is a
// broken project, reported as a failure instead of absorbed as a pass.
// The absence side then asserts the closed gate of SPEC §H2 item 1: the
// whole versionDefines-gated set compiled out atomically, no OpWeave
// type carries the pipeline [CliCommand] surface, and the live Registry
// still builds and registers the standard catalog plus the AT-01 custom
// Operation — while the installed side asserts the inverse, so the
// absence checks can never pass vacuously.
//
// The "every [Core], EditMode and structural test passes" half of AT-04
// is the suite result of the no-Pipeline run itself, not a self-check a
// fixture can perform; PipelineReferenceIsolationTests holds the §H2
// item-7 reference boundary in both configurations.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The AT-04 no-Pipeline acceptance checks of SPEC §T.3: the
    /// com.unity.pipeline gate closes atomically and the product still
    /// works — asserted in whichever configuration the run carries.
    /// </summary>
    internal sealed class At04NoPipelineTests
    {
        private const string PipelinePackageName = "com.unity.pipeline";
        private const string AdapterAssemblyName = "OpWeave.Pipeline";

        // Every assembly whose existence the com.unity.pipeline
        // versionDefines + defineConstraints gate decides (SPEC §H2
        // item 1): the adapter and its EditMode tests pin the supported
        // 0.9.1-exp.1 expression, the L-01 probe and the deliberately
        // violating reference-isolation fixture match any version — the
        // expressions differ but the installed set is one, so the gate
        // must produce one outcome for all of them.
        private static readonly string[] GatedAssemblies =
        {
            "OpWeave.Pipeline",
            "OpWeave.Pipeline.Tests",
            "OpWeaveProbes.Pipeline",
            "OpWeaveFixtures.PipelineReference",
        };

        // The SPEC §H1 command count the adapter declares under group
        // "opweave" when the package is installed — the non-vacuity
        // bound: an adapter that compiled but registered nothing would
        // let the absence assertions pass on a surface that never
        // existed. The detailed name/flag contract is
        // PipelineCommandSurfaceTests' own check.
        private const int OpweaveCommandCount = 14;

        private const string StandardOp = "unity.gameobject.create";
        private const string CustomOp = "proj.transform.reset";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the quiet
        // drain reaches it through reflection (the At01AgentStepsTests
        // probe, T-W2.4-07). A reload already armed behind the runner's
        // reload lock surfaces here while isCompiling is false — the
        // deferred state an isCompiling-only check misses.
        private const string ScriptReloadProbeType =
            "UnityEditorInternal.InternalEditorUtility";
        private const string ScriptReloadProbeMethod =
            "IsScriptReloadRequested";

        private static readonly MethodInfo? s_scriptReloadRequested =
            typeof(EditorApplication).Assembly
                .GetType(ScriptReloadProbeType)
                ?.GetMethod(
                    ScriptReloadProbeMethod,
                    BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

        // Non-null and shaped as OneTimeSetUp proved — the fixture does
        // not run at all on a Unity version whose probe is absent.
        private static bool ScriptReloadRequested() =>
            (bool)s_scriptReloadRequested!.Invoke(null, null)!;

        // The state a queued compile/reload reports while the runner's
        // lock holds it: a running script compile always ends in a
        // reload queued behind the lock, and a requested reload is
        // already pending — either is one the drain must cross, never
        // ride into the registry build below.
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // The pending probe the quiet drain trusts is only as strong as
        // its shape: prove it once up front (the At01AgentStepsTests
        // pattern, T-W2.4-07) so the fixture fails here — before any
        // test — instead of silently degrading to an isCompiling-only
        // check that misses a requested reload armed behind the lock.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var probe = s_scriptReloadRequested;
            if (probe is null ||
                !probe.IsStatic ||
                probe.GetParameters().Length != 0 ||
                probe.ReturnType != typeof(bool))
            {
                Assert.Fail(
                    "the domain-reload pending probe is unavailable on " +
                    "this Unity version — the quiet drain cannot prove a " +
                    "deferred reload is drained");
            }
        }

        // ── configuration consistency ─────────────────────────────────

        // The AT-04 run configuration must tell one story across the
        // manifest declaration, the resolved package set and the
        // compiled adapter. A mismatch is a broken project — a
        // declared-but-unresolved package or a resolved package whose
        // adapter did not compile — and fails the run rather than
        // reading as either configuration.
        [Test]
        public void PipelinePresence_ManifestResolvedPackageAndAdapterAgree()
        {
            var declared = ManifestDeclaresPipeline();
            var resolved = PipelinePackageResolved();
            var compiled =
                AssemblyReferenceInspector.FindAssembly(AdapterAssemblyName) is not null;
            Assert.That(resolved, Is.EqualTo(declared),
                declared
                    ? "manifest.json declares " + PipelinePackageName +
                      " but PackageInfo cannot resolve it — a broken installation, not a Pipeline-free project"
                    : "PackageInfo resolves " + PipelinePackageName +
                      " that manifest.json does not declare — a leaked package, not a Pipeline-free project");
            Assert.That(compiled, Is.EqualTo(resolved),
                resolved
                    ? PipelinePackageName + " is installed but " +
                      AdapterAssemblyName + " is not compiled — the §H2 gate failed closed in the Pipeline configuration"
                    : AdapterAssemblyName + " is compiled but " +
                      PipelinePackageName + " is not installed — the §H2 gate failed open");
        }

        // ── SPEC §H2 item 1: the gated set follows the gate atomically ──

        // The versionDefines gate is all-or-nothing: with the package
        // absent, every gated assembly compiles out (the AT-04 pass
        // condition "OpWeave.Pipeline is not compiled" and its
        // companions); with it installed, every one compiles — otherwise
        // the absence side proves nothing.
        [Test]
        public void GatedAssemblies_FollowPipelinePresenceAtomically()
        {
            var resolved = PipelinePackageResolved();
            foreach (var name in GatedAssemblies)
            {
                var assembly = AssemblyReferenceInspector.FindAssembly(name);
                if (resolved)
                {
                    Assert.That(assembly, Is.Not.Null,
                        name + " must compile while " + PipelinePackageName +
                        " is installed — a stuck-closed gate makes the absence assertions vacuous");
                }
                else
                {
                    Assert.That(assembly, Is.Null,
                        name + " compiled without " + PipelinePackageName +
                        " — its versionDefines/defineConstraints gate failed open (SPEC §H2 item 1)");
                }
            }
        }

        // ── fail closed: no OpWeave type keeps the command surface ────

        // Without the adapter the `unity command opweave` surface must
        // exist nowhere: the [CliCommand] attribute type lives in
        // Unity.Pipeline, so an attribute surviving on any OpWeave*
        // method means a reference outside OpWeave.Pipeline — the §H2
        // item-7 boundary PipelineReferenceIsolationTests enforces at
        // the metadata-reference level. Scanned on the loaded OpWeave*
        // assemblies; attribute data is read as metadata, so no
        // Pipeline type is loaded or named here.
        [Test]
        public void OpWeaveCliSurface_ExistsOnlyWithTheAdapter()
        {
            var adapter = AssemblyReferenceInspector.FindAssembly(AdapterAssemblyName);
            var surfaces = new List<string>();
            var adapterSurfaces = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = assembly.GetName().Name;
                if (string.IsNullOrEmpty(name) ||
                    !name!.StartsWith("OpWeave", StringComparison.Ordinal))
                {
                    continue;
                }
                foreach (var hit in CliCommands(assembly))
                {
                    surfaces.Add(name + ": " + hit);
                    if (string.Equals(name, AdapterAssemblyName, StringComparison.Ordinal))
                    {
                        adapterSurfaces.Add(hit);
                    }
                }
            }

            if (adapter is null)
            {
                Assert.That(surfaces, Is.Empty,
                    AdapterAssemblyName + " is not compiled but these OpWeave " +
                    "methods still carry the pipeline command surface: " +
                    string.Join(", ", surfaces));
                return;
            }

            // Only the adapter's own surface is counted here — the L-01
            // probe assembly legitimately registers extra opweave-group
            // probe commands when the package is installed.
            Assert.That(adapterSurfaces.Count, Is.EqualTo(OpweaveCommandCount),
                AdapterAssemblyName + " must declare the " + OpweaveCommandCount +
                " SPEC §H1 commands under group \"opweave\"; found " +
                (adapterSurfaces.Count == 0 ? "none" : string.Join(", ", adapterSurfaces)));
            Assert.That(adapterSurfaces.All(s => s.StartsWith("opweave ", StringComparison.Ordinal)),
                Is.True,
                "every OpWeave pipeline command must sit under group \"opweave\": " +
                string.Join(", ", adapterSurfaces));
        }

        // ── the product still works: live Registry smoke ───────────────

        // AT-04's substance: the Registry builds and registers the
        // standard catalog plus the project's custom Operation in the
        // same configuration the rest of the suite runs green — the
        // "OpWeave still works" half of the T-W3.3-02 boundary, asserted
        // in both configurations. [UnityTest] for the same reason
        // SearchServiceLiveRegistryTests is: the §C3 stage-1 seam reads
        // the live compile state, so the build runs after the pending
        // import drains.
        [UnityTest]
        public IEnumerator LiveRegistry_BuildsAndRegistersOperations()
        {
            // UTF resumes a reload-crossed [UnityTest] enumerator at its
            // yield with every local reset — a null waiter is that
            // resume, not a fresh start: rebuild the idempotent wait
            // and relay on instead of touching the dead reference (the
            // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07).
            IEnumerator? waiter = null;
            for (;;)
            {
                waiter ??= WaitForQuietEditor();
                if (!waiter.MoveNext())
                {
                    break;
                }
                yield return waiter.Current;
            }

            var snapshot = LiveSnapshot();
            var ids = snapshot.Operations.Select(o => o.Descriptor.Id).ToList();
            Assert.That(ids, Does.Contain(StandardOp),
                "the standard catalog must register without the Pipeline adapter");
            Assert.That(ids, Does.Contain(CustomOp),
                "the project custom Operation must register without the Pipeline adapter");
        }

        // ── helpers ────────────────────────────────────────────────────

        private static bool ManifestDeclaresPipeline()
        {
            var doc = JObject.Parse(File.ReadAllText(ManifestPath));
            var dependencies = doc["dependencies"] as JObject;
            Assert.That(dependencies, Is.Not.Null,
                "manifest.json has no \"dependencies\" object");
            return dependencies!.Property(PipelinePackageName) is not null;
        }

        private static bool PipelinePackageResolved()
        {
            try
            {
                return UnityEditor.PackageManager.PackageInfo
                    .FindForPackageName(PipelinePackageName) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static RegistrySnapshot LiveSnapshot()
        {
            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale, "the live registry build must not be stale");
            Assert.IsNotNull(build.Snapshot, "the live registry must publish a snapshot");
            return build.Snapshot!;
        }

        // The sanctioned bounded conditional drain (the
        // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07) replaces
        // the unconditional `yield return new RecompileScripts(false, false)`
        // residual: that instruction's Perform requests a compile and
        // unlocks the runner's reload lock even when nothing is queued —
        // and with ExpectDomainReload=false UTF keeps no resume state,
        // so a still-pending predecessor reload crossing the unlock
        // abandons the test on cleared locals. Here a pending compile or
        // requested reload is crossed only at DrainedScriptReload — the
        // bounded ExpectDomainReload boundary the caller's relay hands
        // to UTF at the test body's own top level, where the captured PC
        // is correct — and everything else settles on bounded plain
        // frames: a queued compile surfaces as isCompiling a frame or
        // two after Refresh returns, so the wait only trusts a sustained
        // quiet spell and a periodic Refresh flushes the import queue so
        // a queued compile surfaces inside the window, never behind it.
        private static IEnumerator WaitForQuietEditor()
        {
            AssetDatabase.Refresh();
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var nextRefreshAt = double.NegativeInfinity;
            var quietAt = -1.0;
            for (;;)
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "At04NoPipelineTests.WaitForQuietEditor timed out " +
                        "after " + WaitTimeoutSec +
                        " s draining the armed editor state " +
                        "(scriptReloadRequested: " + ScriptReloadRequested() +
                        ", isCompiling: " + EditorApplication.isCompiling +
                        ", isUpdating: " + EditorApplication.isUpdating + ")");
                }
                if (ReloadPending())
                {
                    // A running or requested compile ends in a reload
                    // queued behind the runner's lock — cross it at the
                    // bounded sanctioned boundary, never ride it into
                    // the registry build.
                    quietAt = -1.0;
                    yield return new DrainedScriptReload(
                        "WaitForQuietEditor", WaitTimeoutSec);
                    continue;
                }
                if (EditorApplication.isUpdating)
                {
                    // An import still in flight may surface a compile —
                    // no quiet counts while it runs.
                    quietAt = -1.0;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - quietAt >=
                         QuietSettleSec)
                {
                    break;
                }
                if (EditorApplication.timeSinceStartup >= nextRefreshAt)
                {
                    AssetDatabase.Refresh();
                    nextRefreshAt = EditorApplication.timeSinceStartup + 1.0;
                }
                yield return null;
            }
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the live registry cannot build over a failed script compile");
            Assert.IsFalse(EditorApplication.isCompiling,
                "the live registry cannot build while a compile is in flight");
            Assert.IsFalse(ScriptReloadRequested(),
                "the live registry cannot build while a script reload is pending");
        }

        // The bounded reload-crossing instruction — the
        // WaitForDomainReload.Perform wait with a deadline, run by UTF
        // through the IEditModeTestYieldInstruction interface so
        // ExpectDomainReload arms the runner's reload bookkeeping
        // exactly as with UTF's own instructions. It is yielded only
        // after ReloadPending() proved a real reload is armed, so the
        // unlock below always meets a queued reload and can never strand
        // the lock released on an idle Editor — the failure mode of the
        // unconditional RecompileScripts(false, false) unlock it
        // replaces. The one path where no reload can follow — a failed
        // compile — re-locks before failing.
        private sealed class DrainedScriptReload : IEditModeTestYieldInstruction
        {
            private readonly string m_phase;
            private readonly double m_timeoutSec;

            internal DrainedScriptReload(string phase, double timeoutSec)
            {
                m_phase = phase;
                m_timeoutSec = timeoutSec;
            }

            public bool ExpectDomainReload => true;
            public bool ExpectedPlaymodeState => false;

            public IEnumerator Perform()
            {
                // UTF yields the same first frame: the runner must see
                // the yielded instruction and arm its expected-domain-
                // reload bookkeeping before the reload can start.
                yield return null;

                var deadline =
                    EditorApplication.timeSinceStartup + m_timeoutSec;

                AssetDatabase.Refresh();
                EditorApplication.UnlockReloadAssemblies();

                // Wait out the armed state as WaitForDomainReload.Perform
                // does — while a script reload stays requested or a
                // compile runs — but bounded: the reload itself kills
                // the domain mid-wait, so a deadline expiry can only
                // mean the proven pending state never crossed.
                while (ReloadPending())
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        EditorApplication.LockReloadAssemblies();
                        Assert.Fail(
                            "At04NoPipelineTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "At04NoPipelineTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // "<group> <name>" of every [CliCommand] on the assembly's static
        // methods — the PipelineCommandSurfaceTests shape, applied to
        // assemblies that may legitimately lack the attribute type.
        private static List<string> CliCommands(Assembly assembly)
        {
            var hits = new List<string>();
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }
            catch (Exception)
            {
                return hits;
            }

            const BindingFlags declared =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var type in types)
            {
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(declared);
                }
                catch (Exception)
                {
                    continue;
                }
                foreach (var method in methods)
                {
                    IList<CustomAttributeData> attributes;
                    try
                    {
                        attributes = method.GetCustomAttributesData();
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    foreach (var data in attributes)
                    {
                        string? attributeName;
                        try
                        {
                            attributeName = data.AttributeType.FullName;
                        }
                        catch (Exception)
                        {
                            // An attribute whose type cannot resolve is
                            // itself a suspect surface — report it so the
                            // absence side fails closed instead of
                            // skipping it.
                            hits.Add("? <unresolved attribute on " + method.Name + ">");
                            continue;
                        }
                        if (attributeName != "Unity.Pipeline.Commands.CliCommandAttribute" &&
                            attributeName != "Unity.Pipeline.Commands.CliCommand")
                        {
                            continue;
                        }
                        var args = data.ConstructorArguments;
                        var group = args.Count > 0 ? args[0].Value as string : null;
                        var command = args.Count > 1 ? args[1].Value as string : null;
                        hits.Add((group ?? "?") + " " + (command ?? method.Name));
                    }
                }
            }
            return hits;
        }
    }
}
