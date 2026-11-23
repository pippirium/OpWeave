#nullable enable
// T-W2.2-01 [Edit] tests: SPEC G0 assembly resolution, Editor-only eligibility
// and origin (SPEC C3 rule 2/origin, C0.2), plus the SPEC T.4 L-10
// [CallerFilePath] provenance observation for Assets / Packages / precompiled
// DLL fixtures. Fixtures live in TestProject/Assets/OpWeaveFixtures/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using CompilationAssembly = UnityEditor.Compilation.Assembly;
using ReflectionAssembly = System.Reflection.Assembly;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class EditorAssemblyResolverTests
    {
        // ---------- fixture locations (TestProject/Assets/OpWeaveFixtures/) ----------

        private const string EditorFixtureAssemblyName = "OpWeaveFixtures.Editor";
        private const string RuntimeFixtureAssemblyName = "OpWeaveFixtures.Runtime";
        private const string PluginFixtureAssemblyName = "OpWeave.Plugin";
        private const string PlayerFixtureAssemblyName = "OpWeave.Player";
        private const string UnreferencedFixtureAssemblyName = "OpWeave.Unreferenced";
        private const string ForeignFixtureAssemblyName = "OpWeave.Foreign";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ForeignDllPath =>
            Path.Combine(ProjectRoot, "Assets", "OpWeaveFixtures", "Plugins~", "OpWeave.Foreign.dll");

        private static ReflectionAssembly LoadByName(string name)
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == name);
            if (loaded is not null)
            {
                return loaded;
            }
            return ReflectionAssembly.Load(new AssemblyName(name));
        }

        private static string ReadBakedCallerFilePath(ReflectionAssembly assembly, string typeName)
        {
            var type = assembly.GetType(typeName);
            Assert.That(type, Is.Not.Null, $"fixture type {typeName} missing in {assembly.GetName().Name}");
            var property = type!.GetProperty("BakedCallerFilePath", BindingFlags.Public | BindingFlags.Static);
            Assert.That(property, Is.Not.Null, $"BakedCallerFilePath missing on {typeName}");
            var value = property!.GetValue(null);
            Assert.That(value, Is.TypeOf<string>().And.Not.EqualTo(""));
            return (string)value!;
        }

        // ---------- G0: source-built assemblies resolve uniquely ----------

        [Test]
        public void StandardPackageAssembly_ResolvesEditorOnlyStandard()
        {
            var result = EditorAssemblyResolver.Resolve(typeof(EditorAssemblyResolver).Assembly);

            Assert.That(result.Ok, Is.True, () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var provenance = result.Value!;
            Assert.That(provenance.AssemblyName, Is.EqualTo("OpWeave.UnityAdapter"));
            Assert.That(provenance.IsPrecompiled, Is.False);
            Assert.That(provenance.CompilationAssembly, Is.Not.Null);
            Assert.That(provenance.PackageId, Is.EqualTo(EditorAssemblyResolver.StandardPackageId));
            Assert.That(provenance.Origin, Is.EqualTo(OperationOrigin.Standard));
        }

        [Test]
        public void CorePackageAssembly_ResolvesEditorOnlyStandard()
        {
            var result = EditorAssemblyResolver.Resolve(typeof(OperationOrigin).Assembly);

            Assert.That(result.Ok, Is.True, () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(result.Value!.Origin, Is.EqualTo(OperationOrigin.Standard));
            Assert.That(result.Value.PackageId, Is.EqualTo("com.opweave.workflow"));
        }

        [Test]
        public void PackageTestAssembly_ResolvesStandard()
        {
            // OpWeave.UnityAdapter.Tests also lives inside com.opweave.workflow.
            var result = EditorAssemblyResolver.Resolve(GetType().Assembly);

            Assert.That(result.Ok, Is.True, () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(result.Value!.Origin, Is.EqualTo(OperationOrigin.Standard));
        }

        [Test]
        public void ProjectEditorAssembly_ResolvesEditorOnlyCustom()
        {
            var fixture = LoadByName(EditorFixtureAssemblyName);
            var result = EditorAssemblyResolver.Resolve(fixture);

            Assert.That(result.Ok, Is.True, () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var provenance = result.Value!;
            Assert.That(provenance.IsPrecompiled, Is.False);
            Assert.That(provenance.CompilationAssembly, Is.Not.Null);
            Assert.That(provenance.PackageId, Is.Null);
            Assert.That(provenance.Origin, Is.EqualTo(OperationOrigin.Custom));
            Assert.That(Path.GetFileName(provenance.CompiledAssemblyPath), Is.EqualTo(EditorFixtureAssemblyName + ".dll"));
        }

        [Test]
        public void ResolveForType_ResolvesDefiningAssembly()
        {
            var fixtureType = LoadByName(EditorFixtureAssemblyName).GetType("OpWeave.Fixtures.EditorFixtureMarker");
            Assert.That(fixtureType, Is.Not.Null);

            var result = EditorAssemblyResolver.ResolveForType(fixtureType!);

            Assert.That(result.Ok, Is.True, () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(result.Value!.Origin, Is.EqualTo(OperationOrigin.Custom));
        }

        // ---------- G0: not-Editor-only and unresolvable assemblies are rejected ----------

        [Test]
        public void RuntimeAssembly_IsRejected()
        {
            var fixture = LoadByName(RuntimeFixtureAssemblyName);

            var result = EditorAssemblyResolver.Resolve(fixture);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics.Select(d => d.Code), Has.All.EqualTo(ErrorCodes.OW_OP_017));
        }

        [Test]
        public void EditorOnlyPrecompiledPlugin_ResolvesCustom()
        {
            // OpWeave.Plugin.dll under Assets/OpWeaveFixtures/Plugins/Editor/:
            // an imported Editor-only precompiled plugin.
            var fixture = LoadByName(PluginFixtureAssemblyName);
            Assert.That(fixture.Location, Does.Contain("OpWeaveFixtures").IgnoreCase);

            var result = EditorAssemblyResolver.Resolve(fixture);

            Assert.That(result.Ok, Is.True, () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var provenance = result.Value!;
            Assert.That(provenance.IsPrecompiled, Is.True);
            Assert.That(provenance.CompilationAssembly, Is.Null);
            Assert.That(provenance.Origin, Is.EqualTo(OperationOrigin.Custom));
        }

        [Test]
        public void PlayerReferencedPrecompiledPlugin_IsRejected()
        {
            // OpWeave.Player.dll under Assets/OpWeaveFixtures/Plugins/: all
            // platforms, so it enters the Player compilation.
            var fixture = LoadByName(PlayerFixtureAssemblyName);

            var result = EditorAssemblyResolver.Resolve(fixture);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics.Select(d => d.Code), Has.All.EqualTo(ErrorCodes.OW_OP_017));
        }

        [Test]
        public void PlayerCompatibleUnreferencedPlugin_IsRejected()
        {
            // OpWeave.Unreferenced.dll under Assets/OpWeaveFixtures/Plugins/:
            // imported compatible with Editor and all Player platforms but
            // with Auto Reference disabled (isExplicitlyReferenced: 1), so no
            // compilation assembly references it. Absence of a Player
            // reference does not prove Editor-only; the plugin importer does,
            // and it says this DLL is Player-compatible — reject (SPEC G0).
            var fixture = LoadByName(UnreferencedFixtureAssemblyName);
            Assert.That(fixture.Location, Does.Contain("OpWeaveFixtures").IgnoreCase);

            var result = EditorAssemblyResolver.Resolve(fixture);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics.Select(d => d.Code), Has.All.EqualTo(ErrorCodes.OW_OP_017));
            Assert.That(result.Diagnostics[0].Message, Does.Contain("provably Editor-only"));
        }

        [Test]
        public void UnresolvablePrecompiledDll_IsRejected()
        {
            // OpWeave.Foreign.dll lives in Plugins~/ (never imported) and is
            // loaded by hand: no compilation entry, no reference records — it
            // must never be assumed Editor-only.
            Assert.That(File.Exists(ForeignDllPath), Is.True, "fixture DLL missing: " + ForeignDllPath);
            var fixture = ReflectionAssembly.LoadFrom(ForeignDllPath);

            var result = EditorAssemblyResolver.Resolve(fixture);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics.Select(d => d.Code), Has.All.EqualTo(ErrorCodes.OW_OP_017));
            Assert.That(result.Diagnostics[0].Message, Does.Contain("source distribution"));
        }

        [Test]
        public void ByteLoadedAssembly_IsRejected()
        {
            var bytes = File.ReadAllBytes(ForeignDllPath);
            var fixture = ReflectionAssembly.Load(bytes);

            var result = EditorAssemblyResolver.Resolve(fixture);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics.Select(d => d.Code), Has.All.EqualTo(ErrorCodes.OW_OP_017));
        }

        [Test]
        public void SameNamedForeignCopy_IsRejected()
        {
            // A byte copy of a compiled DLL at a foreign path shares the
            // compilation assembly's name but not its output path; it must not
            // resolve to the compilation product (SPEC G0 name + outputPath).
            var outputPath = Path.Combine(ProjectRoot, "Library", "ScriptAssemblies", "OpWeave.UnityAdapter.dll");
            Assert.That(File.Exists(outputPath), Is.True, "compiled assembly missing: " + outputPath);
            var fixture = ReflectionAssembly.Load(File.ReadAllBytes(outputPath));
            Assert.That(fixture.GetName().Name, Is.EqualTo("OpWeave.UnityAdapter"));
            Assert.That(fixture.Location, Is.Not.EqualTo(outputPath).IgnoreCase);

            var result = EditorAssemblyResolver.Resolve(fixture);

            Assert.That(result.Ok, Is.False);
            Assert.That(result.Diagnostics.Select(d => d.Code), Has.All.EqualTo(ErrorCodes.OW_OP_017));
        }

        // ---------- T.4 L-10: [CallerFilePath] provenance, display only ----------

        [Test]
        public void CallerFilePath_AssetsFixture_ResolvesProjectRelative()
        {
            var fixture = LoadByName(EditorFixtureAssemblyName);
            var raw = ReadBakedCallerFilePath(fixture, "OpWeave.Fixtures.EditorFixtureMarker");
            var relative = EditorAssemblyResolver.ToProjectRelativePath(raw);

            Debug.Log($"L10 Assets: raw=\"{raw}\" -> relative=\"{relative ?? "<null>"}\"");
            Assert.That(relative, Is.EqualTo("Assets/OpWeaveFixtures/Editor/EditorFixtureMarker.cs"));
        }

        [Test]
        public void CallerFilePath_PackageFixture_ResolvesProjectRelative()
        {
            // This test file itself sits inside Packages/com.opweave.workflow/.
            var raw = CaptureCallerFilePathFromPackageSource();
            var relative = EditorAssemblyResolver.ToProjectRelativePath(raw);

            Debug.Log($"L10 Package: raw=\"{raw}\" -> relative=\"{relative ?? "<null>"}\"");
            Assert.That(relative, Is.Not.Null);
            Assert.That(relative, Does.StartWith("Packages/com.opweave.workflow/").IgnoreCase);
        }

        [Test]
        public void CallerFilePath_PackageCacheForm_MapsUnderLibrary()
        {
            // A CallerFilePath baked for a registry-package source points into
            // Library/PackageCache at build time; it is still inside the project.
            var synthetic = Path.Combine(ProjectRoot, "Library", "PackageCache", "com.example.pkg@1.0.0", "Editor", "Op.cs");
            var relative = EditorAssemblyResolver.ToProjectRelativePath(synthetic);

            Debug.Log($"L10 PackageCache: raw=\"{synthetic}\" -> relative=\"{relative ?? "<null>"}\"");
            Assert.That(relative, Does.StartWith("Library/PackageCache/"));
        }

        [Test]
        public void CallerFilePath_ForeignDll_NotProjectResolvable()
        {
            Assert.That(File.Exists(ForeignDllPath), Is.True, "fixture DLL missing: " + ForeignDllPath);
            var fixture = ReflectionAssembly.LoadFrom(ForeignDllPath);
            var raw = ReadBakedCallerFilePath(fixture, "OpWeave.Fixtures.OpWeave_ForeignCallerFilePath");
            var relative = EditorAssemblyResolver.ToProjectRelativePath(raw);

            Debug.Log($"L10 Precompiled: raw=\"{raw}\" -> relative=\"{relative ?? "<null>"}\"");
            Assert.That(raw, Does.Contain("ForeignMarker.cs"));
            Assert.That(relative, Is.Null, "a foreign build path must not resolve inside the project");
        }

        [Test]
        public void CallerFilePath_NullOrEmpty_NotResolvable()
        {
            Assert.That(EditorAssemblyResolver.ToProjectRelativePath(null), Is.Null);
            Assert.That(EditorAssemblyResolver.ToProjectRelativePath(""), Is.Null);
        }

        // ---------- T-W2.2-09: per-build memoization of the compilation-assembly queries ----------

        // The injectable query seam's counting fake: forwards to the live
        // CompilationPipeline and records how often each kind is queried.
        private sealed class CountingQueries : EditorAssemblyResolver.ICompilationAssemblyQueries
        {
            private readonly Dictionary<AssembliesType, int> calls = new Dictionary<AssembliesType, int>();

            public CompilationAssembly[] GetAssemblies(AssembliesType assembliesType)
            {
                calls.TryGetValue(assembliesType, out var count);
                calls[assembliesType] = count + 1;
                return CompilationPipeline.GetAssemblies(assembliesType);
            }

            public int Calls(AssembliesType assembliesType) =>
                calls.TryGetValue(assembliesType, out var count) ? count : 0;
        }

        [Test]
        public void CompilationAssemblyScope_QueriesEachKindOnceWithinOneScope()
        {
            // Compiled-Editor, precompiled and rejected assemblies all run
            // the §G0 queries; inside one scope each GetAssemblies kind is
            // still queried exactly once (T-W2.2-09 acceptance).
            var targets = new[]
            {
                typeof(EditorAssemblyResolver).Assembly,
                typeof(OperationOrigin).Assembly,
                LoadByName(EditorFixtureAssemblyName),
                LoadByName(PluginFixtureAssemblyName),
                LoadByName(PlayerFixtureAssemblyName),
            };
            var counting = new CountingQueries();

            using (EditorAssemblyResolver.InstallQueries(counting))
            using (EditorAssemblyResolver.OpenCompilationAssemblyScope())
            {
                foreach (var target in targets)
                {
                    EditorAssemblyResolver.Resolve(target);
                }
            }

            Assert.That(counting.Calls(AssembliesType.Editor), Is.EqualTo(1));
            Assert.That(counting.Calls(AssembliesType.Player), Is.EqualTo(1));
        }

        [Test]
        public void CompilationAssemblyScope_OutsideScopeQueriesEachResolveAndNextScopeRequeries()
        {
            var counting = new CountingQueries();
            var target = typeof(EditorAssemblyResolver).Assembly;
            using (EditorAssemblyResolver.InstallQueries(counting))
            {
                // Outside a scope the call pattern is unchanged: every
                // resolve queries both kinds, as today.
                EditorAssemblyResolver.Resolve(target);
                EditorAssemblyResolver.Resolve(target);
                Assert.That(counting.Calls(AssembliesType.Editor), Is.EqualTo(2));
                Assert.That(counting.Calls(AssembliesType.Player), Is.EqualTo(2));

                // A disposed scope does not leak its cache: the next
                // scope queries again.
                using (EditorAssemblyResolver.OpenCompilationAssemblyScope())
                {
                    EditorAssemblyResolver.Resolve(target);
                    EditorAssemblyResolver.Resolve(target);
                }
                using (EditorAssemblyResolver.OpenCompilationAssemblyScope())
                {
                    EditorAssemblyResolver.Resolve(target);
                }
                Assert.That(counting.Calls(AssembliesType.Editor), Is.EqualTo(4));
                Assert.That(counting.Calls(AssembliesType.Player), Is.EqualTo(4));

                // Back outside a scope: straight through to the queries.
                EditorAssemblyResolver.Resolve(target);
                Assert.That(counting.Calls(AssembliesType.Editor), Is.EqualTo(5));
                Assert.That(counting.Calls(AssembliesType.Player), Is.EqualTo(5));
            }

            // Disposing the installed queries restores the production
            // default — resolution still works against CompilationPipeline.
            var restored = EditorAssemblyResolver.Resolve(target);
            Assert.That(restored.Ok, Is.True,
                () => string.Join("; ", restored.Diagnostics.Select(d => d.Code + " " + d.Message)));
        }

        [Test]
        public void CompilationAssemblyScope_ProducesTheSameProvenanceAsWithoutScope()
        {
            // T-W2.2-09 acceptance: for every registered standard
            // Operation (every [OpWeaveOperation] TypeCache candidate, the
            // standard set included) and every TestProject fixture type
            // the scope must yield the identical §G0 provenance — or the
            // identical failure.
            var types = new List<Type>();
            foreach (var type in TypeCache.GetTypesWithAttribute<OpWeave.Core.Operations.OpWeaveOperationAttribute>())
            {
                types.Add(type);
            }
            foreach (var name in new[]
                     {
                         EditorFixtureAssemblyName,
                         RuntimeFixtureAssemblyName,
                         PluginFixtureAssemblyName,
                         PlayerFixtureAssemblyName,
                         UnreferencedFixtureAssemblyName,
                     })
            {
                foreach (var type in LoadByName(name).GetTypes())
                {
                    types.Add(type);
                }
            }
            Assert.That(File.Exists(ForeignDllPath), Is.True, "fixture DLL missing: " + ForeignDllPath);
            foreach (var type in ReflectionAssembly.LoadFrom(ForeignDllPath).GetTypes())
            {
                types.Add(type);
            }
            Assert.That(types, Is.Not.Empty);

            var expected = new Result<EditorAssemblyProvenance>[types.Count];
            for (var i = 0; i < types.Count; i++)
            {
                expected[i] = EditorAssemblyResolver.ResolveForType(types[i]);
            }

            using (EditorAssemblyResolver.OpenCompilationAssemblyScope())
            {
                for (var i = 0; i < types.Count; i++)
                {
                    AssertSameProvenance(
                        expected[i],
                        EditorAssemblyResolver.ResolveForType(types[i]),
                        $"{types[i].FullName} ({types[i].Assembly.GetName().Name})");
                }
            }
        }

        private static void AssertSameProvenance(
            Result<EditorAssemblyProvenance> expected,
            Result<EditorAssemblyProvenance> actual,
            string subject)
        {
            Assert.That(actual.Ok, Is.EqualTo(expected.Ok), subject);
            if (!expected.Ok)
            {
                Assert.That(
                    actual.Diagnostics.Select(d => d.Code + " " + d.Message),
                    Is.EqualTo(expected.Diagnostics.Select(d => d.Code + " " + d.Message)),
                    subject);
                return;
            }
            var a = actual.Value!;
            var e = expected.Value!;
            Assert.That(a.AssemblyName, Is.EqualTo(e.AssemblyName), subject);
            Assert.That(a.CompiledAssemblyPath, Is.EqualTo(e.CompiledAssemblyPath), subject);
            Assert.That(a.IsPrecompiled, Is.EqualTo(e.IsPrecompiled), subject);
            Assert.That(a.Origin, Is.EqualTo(e.Origin), subject);
            Assert.That(a.PackageId, Is.EqualTo(e.PackageId), subject);
            Assert.That(a.Package?.Version, Is.EqualTo(e.Package?.Version), subject);
            Assert.That(a.Package?.Source, Is.EqualTo(e.Package?.Source), subject);
            Assert.That(a.Package?.ResolvedRevision, Is.EqualTo(e.Package?.ResolvedRevision), subject);
            Assert.That(a.Package?.RequestedRevision, Is.EqualTo(e.Package?.RequestedRevision), subject);
            Assert.That(a.Package?.SourceRepository, Is.EqualTo(e.Package?.SourceRepository), subject);
            Assert.That(a.CompilationAssembly?.name, Is.EqualTo(e.CompilationAssembly?.name), subject);
            Assert.That(a.CompilationAssembly?.outputPath, Is.EqualTo(e.CompilationAssembly?.outputPath), subject);
        }

        private static string CaptureCallerFilePathFromPackageSource(
            [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") => sourceFile;
    }
}
