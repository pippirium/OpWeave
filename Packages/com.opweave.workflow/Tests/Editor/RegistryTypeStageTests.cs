#nullable enable

// T-W2.2-03 [Edit] tests: local:T-W2.2-03:registry-stages-1-5 — SPEC §C3
// stages 1–5 over real fixture candidates. The probe providers live in this
// test assembly (project candidates, Provider ID = CLR FullName);
// com.opweave.fixture.valid exercises the manifest-declared Provider ID
// binding and com.opweave.fixture.unitymin the §X.3.2 environment
// eligibility rejection — its declared Unity minimum exceeds the running
// Editor, so its provider is never constructed. The reserved-namespace
// probe is re-attributed non-standard provenance through the
// ResolveProvenance seam because this test assembly is itself inside the
// standard package. A compile-error case marks the result stale without
// constructing anything — once through an injected flag and once through a
// real broken script under Assets/OpWeaveTempCompileError_* driving the
// production EditorUtility.scriptCompilationFailed path; a
// predicate-rejected probe's constructor never runs; a colliding Type ID
// invalidates both candidates and nothing else; the production startup
// hook retains a real build. The domain-reload [UnityTest] re-runs the
// build across a real reload (SPEC §T.3 pattern).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class RegistryTypeStageTests
    {
        private const string ValidPackage = "com.opweave.fixture.valid";
        private const string ValidProviderId = "com.opweave.fixture.valid.types";
        private const string UnityMinProviderId = "com.opweave.fixture.unitymin.types";
        private const string UnityMinProviderType = "com.opweave.fixture.unitymin.UnityMinProvider";
        private const string TempCompileErrorPrefix = "OpWeaveTempCompileError_";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ReloadMarkerPath =>
            Path.Combine(ProjectRoot, "Packages", ValidPackage, "Editor", "RegistryBuildReloadMarker.cs");

        private const string ReloadMarkerSource =
            "#nullable enable\n" +
            "namespace com.opweave.fixture.valid\n{\n" +
            "    internal static class RegistryBuildReloadMarker\n    {\n" +
            "        public const int Value = 1;\n    }\n" +
            "}\n";

        private static object? s_domainSentinel;
        private static void ArmDomainSentinel() => s_domainSentinel = new object();
        private static bool DomainSentinelAlive => s_domainSentinel is not null;

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            var dirty = false;
            if (File.Exists(ReloadMarkerPath))
            {
                File.Delete(ReloadMarkerPath);
                dirty = true;
            }
            var meta = ReloadMarkerPath + ".meta";
            if (File.Exists(meta))
            {
                File.Delete(meta);
                dirty = true;
            }
            if (DeleteTempCompileErrorFolders())
            {
                dirty = true;
            }
            if (dirty)
            {
                AssetDatabase.Refresh();
                yield return new RecompileScripts(false);
            }
        }

        // Removes every leftover Assets/OpWeaveTempCompileError_* folder so a
        // failed or interrupted earlier run cannot poison later tests; the
        // compile-error fixture is intentionally never committed.
        private static bool DeleteTempCompileErrorFolders()
        {
            var removed = false;
            foreach (var dir in new DirectoryInfo(Application.dataPath)
                .GetDirectories(TempCompileErrorPrefix + "*"))
            {
                dir.Delete(recursive: true);
                var meta = dir.FullName + ".meta";
                if (File.Exists(meta))
                {
                    File.Delete(meta);
                }
                removed = true;
            }
            return removed;
        }

        // ── Fixture providers: project candidates inside the test assembly ──

        private static TypeDefinition StringDef(string id) =>
            new TypeDefinition(id, TypeKind.Primitive, 0,
                Array.Empty<string>(), null, "Test fixture type.", StringCodec.Instance);

        [OpWeaveTypeProvider]
        internal sealed class AlphaProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { StringDef("test.fixture.alpha"), StringDef("test.fixture.alpha.b") };
        }

        [OpWeaveTypeProvider]
        internal sealed class BetaProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { StringDef("test.fixture.beta") };
        }

        // Colliding pair: both offer the same Type ID — §B0.1 invalidates both.
        [OpWeaveTypeProvider]
        internal sealed class CollideAProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { StringDef("test.fixture.shared") };
        }

        [OpWeaveTypeProvider]
        internal sealed class CollideBProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { StringDef("test.fixture.shared") };
        }

        // Constructor probe: excluded by an injected predicate — never constructed.
        [OpWeaveTypeProvider]
        internal sealed class RejectedProbeProvider : ITypeProvider
        {
            internal static int s_ctorCalls;
            public RejectedProbeProvider() => s_ctorCalls++;
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { StringDef("test.fixture.rejected") };
        }

        // Eligibility failure: no public parameterless constructor.
        [OpWeaveTypeProvider]
        internal sealed class NoCtorProvider : ITypeProvider
        {
            private NoCtorProvider()
            {
            }

            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { StringDef("test.fixture.noctor") };
        }

        // Reserved-namespace offer from a non-standard provider — screened out.
        [OpWeaveTypeProvider]
        internal sealed class ReservedOfferProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() =>
                new[] { StringDef("opweave.fixture.reserved") };
        }

        private sealed class RejectingPredicate : IExtensionExclusionPredicate
        {
            private readonly Type _rejected;

            internal RejectingPredicate(Type rejected) => _rejected = rejected;

            public string? Exclude(ExtensionCandidate candidate) =>
                candidate.Type == _rejected ? "test predicate exclusion" : null;
        }

        private static RegistryTypeStage.Seams LiveSeams(
            IExtensionExclusionPredicate? exclusion = null)
        {
            var seams = RegistryTypeStage.Production(exclusion ?? AutoExtensionExclusion.Instance);
            return seams with
            {
                // The reserved-namespace probe lives in the standard
                // package's own test assembly, so its real provenance is
                // Standard and the reserved-namespace rule could never fire
                // for it; re-attribute it as a non-standard (project)
                // assembly through the provenance seam.
                ResolveProvenance = type =>
                {
                    var resolved = EditorAssemblyResolver.ResolveForType(type);
                    if (resolved.Ok && type == typeof(ReservedOfferProvider))
                    {
                        var real = resolved.Value!;
                        resolved = Result<EditorAssemblyProvenance>.Success(
                            new EditorAssemblyProvenance(
                                real.AssemblyName, real.CompiledAssemblyPath,
                                real.IsPrecompiled, OperationOrigin.Custom,
                                package: null, real.CompilationAssembly));
                    }
                    return resolved;
                },
            };
        }

        private static RegistryTypeStage.Seams FailingCompileSeams() =>
            new RegistryTypeStage.Seams(
                IsCompiling: () => false,
                ScriptCompilationFailed: () => true,
                OperationTypes: () => TypeCache.GetTypesWithAttribute<OpWeave.Core.Operations.OpWeaveOperationAttribute>(),
                ProviderTypes: () => TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>(),
                ResolveProvenance: type => EditorAssemblyResolver.ResolveForType(type),
                PackageFacts: _ => null,
                Exclusion: AutoExtensionExclusion.Instance,
                ClassRelations: new UnityClassRelationProvider());

        private static TypeRef TR(string id) => TypeRef.Parse(id).Value!;

        private static void AssertFixtureBuild(RegistryTypeBuildResult result)
        {
            Assert.That(result.Stale, Is.False);
            Assert.That(result.Types, Is.Not.Null);
            var registry = result.Types!;

            // Standard types plus the fixture offers.
            Assert.That(registry.Resolve(TR("core.String")).Ok, Is.True);
            Assert.That(registry.Resolve(TR("unity.GameObjectRef")).Ok, Is.True);
            Assert.That(registry.Resolve(TR("test.fixture.alpha")).Ok, Is.True);
            Assert.That(registry.Resolve(TR("test.fixture.beta")).Ok, Is.True);

            // The colliding Type ID invalidates BOTH candidates — it is
            // unresolvable — and nothing else is affected (SPEC §B0.1).
            Assert.That(registry.Resolve(TR("test.fixture.shared")).Ok, Is.False);
            var collisions = result.TypeFailures
                .Where(f => f.Kind == TypeRegistryFailureKind.DuplicateTypeId &&
                    f.TypeId == "test.fixture.shared")
                .ToList();
            Assert.That(collisions, Has.Count.EqualTo(2));
            Assert.That(collisions.All(f => f.ProviderId.EndsWith("Provider")), Is.True);

            // A non-standard provider offering an opweave.* Type ID is
            // screened out as reserved — the offer is absent.
            Assert.That(registry.Resolve(TR("opweave.fixture.reserved")).Ok, Is.False);
            Assert.That(result.TypeFailures
                .Where(f => f.Kind == TypeRegistryFailureKind.ReservedNamespace),
                Has.Some.Matches<TypeRegistryFailure>(f => f.TypeId == "opweave.fixture.reserved"));

            // The ineligible provider produces a failure record and offers nothing.
            Assert.That(registry.Resolve(TR("test.fixture.noctor")).Ok, Is.False);
            var noCtorFailures = result.Failures
                .Where(f => f.TypeFullName == typeof(NoCtorProvider).FullName)
                .ToList();
            Assert.That(noCtorFailures, Has.Count.EqualTo(1));

            // Provider IDs: project candidates take the CLR FullName; the
            // manifest declares the packaged provider's ID (SPEC §X.6).
            var providers = result.Providers;
            Assert.That(providers.Select(p => p.ProviderId),
                Has.Some.EqualTo(typeof(AlphaProvider).FullName)
                    .And.Some.EqualTo(ValidProviderId)
                    .And.Some.EqualTo(CoreTypeProvider.ProviderId)
                    .And.Some.EqualTo(UnityTypeProvider.ProviderId));

            // Operation candidates are collected as metadata, never constructed.
            Assert.That(result.Operations, Has.All.Matches<ExtensionCandidate>(
                c => c.Kind == ExtensionCandidateKind.Operation && c.ProviderId is null));
        }

        [Test]
        public void Build_TypeRegistryMatchesFixtureExpectation()
        {
            var result = RegistryTypeStage.Run(LiveSeams());
            AssertFixtureBuild(result);
        }

        [Test]
        public void Build_IsDeterministicAcrossRuns()
        {
            var first = RegistryTypeStage.Run(LiveSeams());
            var second = RegistryTypeStage.Run(LiveSeams());
            Assert.That(
                second.Types!.All.Select(t => t.Id),
                Is.EqualTo(first.Types!.All.Select(t => t.Id)));
            Assert.That(
                second.TypeFailures.Select(f => f.Kind + "|" + f.TypeId + "|" + f.ProviderId),
                Is.EqualTo(first.TypeFailures.Select(f => f.Kind + "|" + f.TypeId + "|" + f.ProviderId)));
        }

        [Test]
        public void Build_PredicateRejectsProbe_ConstructorNeverRuns()
        {
            RejectedProbeProvider.s_ctorCalls = 0;
            var result = RegistryTypeStage.Run(
                LiveSeams(new RejectingPredicate(typeof(RejectedProbeProvider))));

            Assert.That(RejectedProbeProvider.s_ctorCalls, Is.EqualTo(0),
                "an excluded probe's constructor must never run (SPEC §C3 rule 5)");
            Assert.That(result.Types!.Resolve(TR("test.fixture.rejected")).Ok, Is.False);
            Assert.That(result.Exclusions, Has.Count.EqualTo(1));
            Assert.That(result.Exclusions[0].Candidate.Type,
                Is.EqualTo(typeof(RejectedProbeProvider)));
            Assert.That(result.Exclusions[0].Reason, Is.EqualTo("test predicate exclusion"));
        }

        // The unitymin fixture declares unity.minInclusive 9999.0: the stage
        // must reject the package as environment-ineligible before binding
        // or constructing its provider (SPEC §X.3.2).
        [Test]
        public void Build_UnityMinimumExceeded_RejectsProviderWithoutConstructing()
        {
            var probeType = TypeCache.GetTypesWithAttribute<OpWeaveTypeProviderAttribute>()
                .FirstOrDefault(t => t.FullName == UnityMinProviderType);
            Assert.That(probeType, Is.Not.Null, "unitymin fixture provider was not discovered");
            var ctorCalls = probeType!.GetField(
                "s_ctorCalls", BindingFlags.Public | BindingFlags.Static);
            Assert.That(ctorCalls, Is.Not.Null, "unitymin fixture provider lost its constructor probe");
            ctorCalls!.SetValue(null, 0);

            var result = RegistryTypeStage.Run(LiveSeams());

            Assert.That(ctorCalls!.GetValue(null), Is.EqualTo(0),
                "an Editor-ineligible package's provider must never be constructed (SPEC §X.3.2)");
            Assert.That(result.Providers.Select(p => p.ProviderId),
                Has.None.EqualTo(UnityMinProviderId));
            Assert.That(result.Failures,
                Has.Some.Matches<ExtensionCandidateFailure>(f =>
                    f.Kind == ExtensionCandidateKind.TypeProvider &&
                    f.TypeFullName == UnityMinProviderType &&
                    f.Diagnostics.Any(d => d.Message.Contains("9999.0"))));
        }

        [Test]
        public void Build_CompileFailure_MarksStaleWithoutConstructing()
        {
            RejectedProbeProvider.s_ctorCalls = 0;
            var result = RegistryTypeStage.Run(FailingCompileSeams());

            Assert.That(result.CompilationConsistent, Is.False);
            Assert.That(result.Stale, Is.True);
            Assert.That(result.Types, Is.Null);
            Assert.That(result.Providers, Is.Empty);
            Assert.That(result.Operations, Is.Empty);
            Assert.That(result.TypeFailures, Is.Empty);
            Assert.That(result.Failures, Is.Empty);
            Assert.That(result.Exclusions, Is.Empty);
            Assert.That(RejectedProbeProvider.s_ctorCalls, Is.EqualTo(0),
                "a failed compile must not construct any candidate (SPEC §C3 rule 9)");
        }

        [Test]
        public void Build_Compiling_MarksStale()
        {
            var seams = FailingCompileSeams() with
            {
                ScriptCompilationFailed = () => false,
                IsCompiling = () => true,
            };
            var result = RegistryTypeStage.Run(seams);
            Assert.That(result.Stale, Is.True);
            Assert.That(result.Types, Is.Null);
        }

        // SPEC §C3 rule 9 end to end: a real failed compile — not an
        // injected flag — marks the registry stale through the production
        // EditorUtility.scriptCompilationFailed path. The broken script
        // lives in its own temporary asmdef under
        // Assets/OpWeaveTempCompileError_* so the error stays isolated;
        // UnityTearDown removes the folder and recompiles cleanly whatever
        // this test leaves behind.
        [UnityTest]
        public IEnumerator Build_RealCompileFailure_MarksStaleWithoutConstructing()
        {
            RejectedProbeProvider.s_ctorCalls = 0;

            var folderName =
                TempCompileErrorPrefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            var folder = Path.Combine(Application.dataPath, folderName);
            // The deliberate CS0029 below is a real compiler error log the
            // Unity Test Framework would otherwise report as an unhandled
            // failure. Unity repeats the same compile error a varying number
            // of times, so ignore (not Expect) failing messages across the
            // whole window — the fixture write, the failed recompile and the
            // clean recompile after deleting the fixture — then restore the
            // previous value in finally.
            var previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(
                    Path.Combine(folder, folderName + ".asmdef"),
                    "{\n" +
                    "    \"name\": \"" + folderName + "\",\n" +
                    "    \"includePlatforms\": [\"Editor\"]\n" +
                    "}\n");
                File.WriteAllText(
                    Path.Combine(folder, "BrokenProbe.cs"),
                    "internal static class BrokenProbe\n" +
                    "{\n" +
                    "    public static int Value = \"not an int\";\n" +
                    "}\n");
                AssetDatabase.Refresh();
                yield return new RecompileScripts(
                    expectScriptCompilation: true, expectScriptCompilationSuccess: false);

                Assert.That(EditorUtility.scriptCompilationFailed, Is.True,
                    "the fixture failed to produce the compile error under test");
                var result = RegistryTypeStage.Run(
                    RegistryTypeStage.Production(AutoExtensionExclusion.Instance));

                Assert.That(result.CompilationConsistent, Is.False);
                Assert.That(result.Stale, Is.True);
                Assert.That(result.Types, Is.Null);
                Assert.That(result.Providers, Is.Empty);
                Assert.That(result.Operations, Is.Empty);
                Assert.That(result.TypeFailures, Is.Empty);
                Assert.That(result.Failures, Is.Empty);
                Assert.That(result.Exclusions, Is.Empty);
                Assert.That(RejectedProbeProvider.s_ctorCalls, Is.EqualTo(0),
                    "a real failed compile must not construct any candidate (SPEC §C3 rule 9)");

                // Delete the fixture and recompile to success while failing
                // messages are still ignored, so cleanup cannot be reported
                // as an unhandled failure either; UnityTearDown remains the
                // safety net for a mid-test abort.
                Directory.Delete(folder, recursive: true);
                File.Delete(folder + ".meta");
                AssetDatabase.Refresh();
                yield return new RecompileScripts(
                    expectScriptCompilation: true, expectScriptCompilationSuccess: true);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
            }
        }

        [Test]
        public void StartupHook_RunsStagesOneToFive_AndRetainsBuild()
        {
            var host = OpWeaveRunnerHost.CreateProduction();
            try
            {
                var ran = host.StartupHooks.RunAll();
                Assert.That(ran, Has.Some.EqualTo("registry-build"),
                    "the SPEC §1.4 delayCall list must carry the C3 rebuild hook");
                Assert.That(RegistryBuildHost.Latest, Is.Not.Null);
                Assert.That(RegistryBuildHost.Latest!.Stale, Is.False);
                Assert.That(RegistryBuildHost.Latest.Types!
                    .Resolve(TR("test.fixture.alpha")).Ok, Is.True);
            }
            finally
            {
                host.Dispose();
            }
        }

        // SPEC §T.3: the same build outcome holds after a real domain reload.
        [UnityTest]
        public IEnumerator BuildOutcome_SurvivesDomainReload()
        {
            File.WriteAllText(ReloadMarkerPath, ReloadMarkerSource);
            AssetDatabase.Refresh();
            ArmDomainSentinel();
            yield return new RecompileScripts();
            var guard = 0;
            while (DomainSentinelAlive && guard++ < 10000)
            {
                yield return null;
            }
            Assert.IsFalse(DomainSentinelAlive,
                "the script change did not produce a domain reload");

            RejectedProbeProvider.s_ctorCalls = 0;
            var result = RegistryTypeStage.Run(
                LiveSeams(new RejectingPredicate(typeof(RejectedProbeProvider))));
            AssertFixtureBuild(result);
            Assert.That(RejectedProbeProvider.s_ctorCalls, Is.EqualTo(0));
        }
    }
}
