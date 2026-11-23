#nullable enable
// T-W2.12-02 [Edit] tests: local:T-W2.12-02:upm-provenance — UPM-resolved
// package provenance (package ID, version, source, source repository,
// resolved commit) is recorded on the per-assembly provenance, on the
// extension discovery outcome, in the discovery report's Packages map and
// in the report's per-Operation (id, version) / per-TypeProvider
// (Provider ID) lookups — the keyed provenance form T-W2.2-07 embeds
// into RegistrySnapshot so describe_operation and the Registry tab read
// each registered item's package info from the same snapshot (SPEC §C3).
// Embedded fixture
// packages (committed under
// TestProject/Packages/) prove local-source provenance; a local Git
// fixture repository created by the test (file:// bare repo, ?path=
// subfolder packages — the URL form T-W2.12-05 verified) proves
// Git-source provenance installed by tag and by full commit SHA, with
// self-declared repository metadata present in every fixture to prove it
// is never trusted (SPEC §X.3.2, §G0).
//
// Installing a package with an Editor assembly forces a compile and a
// domain reload. Domain reloads are locked while the runner pumps a test,
// so the reload only fires inside `yield return new WaitForDomainReload()`
// (SPEC §T.3). The yield must sit inline in the test/setUp/tearDown
// method — the saved program counter resumes the method's own enumerator,
// so every local resets at the boundary and post-reload state is
// re-derived, exactly like
// ExtensionDiscoveryTests.DiscoveryOutcome_SurvivesDomainReload.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.TestTools;
using SysProcess = System.Diagnostics.Process;
using SysStartInfo = System.Diagnostics.ProcessStartInfo;
using UpmPackageSource = UnityEditor.PackageManager.PackageSource;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W2.12-02:upm-provenance — provenance fixtures (local and
    /// Git-installed packages).
    /// </summary>
    internal sealed class UpmPackageProvenanceTests
    {
        // ---------- fixture identities ----------

        private const string TagPackage = "com.opweave.fixture.gittag";
        private const string ShaPackage = "com.opweave.fixture.gitsha";
        private const string TagAssemblyName = "OpWeave.Fixture.GitTag.Editor";
        private const string ShaAssemblyName = "OpWeave.Fixture.GitSha.Editor";
        private const string TagName = "vtag1";
        private const string ValidEmbeddedPackage = "com.opweave.fixture.valid";
        private const string ValidEmbeddedAssembly = "OpWeave.Fixture.Valid.Editor";
        private const string ValidEmbeddedProvider = "com.opweave.fixture.valid.ValidProvider";
        private const string FixturePrefix = "com.opweave.fixture.git";

        // Self-declared traps: the fixture package.json ships a fake
        // repository block and its opweave.extension.json ships fake
        // sourceRepository/resolvedRevision keys. Provenance must never
        // report these values (SPEC §X.3.2: UPM resolution only).
        private const string FakeTagRepo = "https://self-declared.invalid/fake-tag.git";
        private const string FakeShaRepo = "https://self-declared.invalid/fake-sha.git";
        private const string FakeRevision = "ffffffffffffffffffffffffffffffffffffffff";

        private const string FixtureDirName = "OpWeaveUpmProvenance";
        private const string SrcDirName = "provenance-src";
        private const string BareDirName = "provenance-repo.git";
        private const string EnvGitExe = "OPWEAVE_LOCAL_GIT_EXE";

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ManifestPath =>
            Path.Combine(ProjectRoot, "Packages", "manifest.json");

        private static string FixtureRoot => Path.Combine(ProjectRoot, "Library", FixtureDirName);
        private static string SrcRepo => Path.Combine(FixtureRoot, SrcDirName);
        private static string BareRepo => Path.Combine(FixtureRoot, BareDirName);
        private static string BareRepoUrl => "file:///" + BareRepo.Replace('\\', '/');
        private static string GitExe =>
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvGitExe))
                ? "git"
                : Environment.GetEnvironmentVariable(EnvGitExe)!;

        // ---------- git fixture construction ----------

        private sealed class GitResult
        {
            public int Exit = -1;
            public string StdOut = "";
            public string StdErr = "";
            public string FirstLine => (StdOut + " " + StdErr).Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        }

        private static GitResult Git(string args, string? workDir)
        {
            var result = new GitResult();
            var psi = new SysStartInfo
            {
                FileName = GitExe,
                Arguments = args,
                WorkingDirectory = workDir ?? ProjectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var proc = SysProcess.Start(psi);
            Assert.That(proc, Is.Not.Null, "git failed to start");
            using (var p = proc!)
            {
                var stdOut = p.StandardOutput.ReadToEndAsync();
                var stdErr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch { /* best effort */ }
                    Assert.Fail("git timed out: " + args);
                }
                result.Exit = p.ExitCode;
                result.StdOut = stdOut.Result;
                result.StdErr = stdErr.Result;
            }
            return result;
        }

        private static void AssertGit(GitResult result, string what) =>
            Assert.That(result.Exit, Is.EqualTo(0),
                $"git {what} failed (exit {result.Exit}): {result.FirstLine}");

        // One fixture package: package.json with a self-declared repository
        // block, an opweave.extension.json with self-declared provenance
        // keys (ignored as audit data, SPEC §X.3.2), an Editor asmdef and a
        // real [OpWeaveTypeProvider] class so the package is a valid
        // extension whose assembly resolves through the G0 path. Every
        // asset ships a committed .meta file: Git-resolved packages are
        // immutable, so the Editor ignores assets that lack one — without
        // them the fixture assembly would never compile (embedded packages
        // get .meta written in place; Git packages cannot).
        private static void WriteFixturePackage(
            string root, string packageId, string assemblyName, string className, string fakeRepo)
        {
            Directory.CreateDirectory(Path.Combine(root, "Editor"));
            WriteAssetMeta(Path.Combine(root, "Editor"), packageId + ":Editor", folder: true);
            File.WriteAllText(Path.Combine(root, "package.json"),
                "{\n" +
                "  \"name\": \"" + packageId + "\",\n" +
                "  \"version\": \"1.0.0\",\n" +
                "  \"displayName\": \"OpWeave provenance fixture\",\n" +
                "  \"unity\": \"6000.0\",\n" +
                "  \"repository\": {\n" +
                "    \"type\": \"git\",\n" +
                "    \"url\": \"" + fakeRepo + "\",\n" +
                "    \"revision\": \"" + FakeRevision + "\"\n" +
                "  }\n" +
                "}\n");
            WriteAssetMeta(Path.Combine(root, "package.json"), packageId + ":package.json");
            File.WriteAllText(Path.Combine(root, "opweave.extension.json"),
                "{\n" +
                "  \"schemaVersion\": \"1.0\",\n" +
                "  \"extensionId\": \"" + packageId + "\",\n" +
                "  \"displayName\": \"OpWeave provenance fixture\",\n" +
                "  \"coreApi\": {\"minInclusive\": \"1.0.0\", \"maxExclusive\": \"2.0.0\"},\n" +
                "  \"unity\": {\"minInclusive\": \"6000.0\"},\n" +
                "  \"assemblies\": [\"" + assemblyName + "\"],\n" +
                "  \"operations\": [],\n" +
                "  \"typeProviders\": [{\"id\": \"" + packageId + ".types\", \"assembly\": \"" + assemblyName + "\"}],\n" +
                "  \"examples\": [],\n" +
                "  \"blueprints\": [],\n" +
                "  \"sourceRepository\": \"" + fakeRepo + "\",\n" +
                "  \"resolvedRevision\": \"" + FakeRevision + "\"\n" +
                "}\n");
            WriteAssetMeta(Path.Combine(root, "opweave.extension.json"),
                packageId + ":opweave.extension.json");
            File.WriteAllText(Path.Combine(root, "Editor", assemblyName + ".asmdef"),
                "{\n" +
                "    \"name\": \"" + assemblyName + "\",\n" +
                "    \"references\": [\n" +
                "        \"OpWeave.Core\"\n" +
                "    ],\n" +
                "    \"includePlatforms\": [\n" +
                "        \"Editor\"\n" +
                "    ],\n" +
                "    \"excludePlatforms\": [],\n" +
                "    \"autoReferenced\": true\n" +
                "}\n");
            WriteAssetMeta(Path.Combine(root, "Editor", assemblyName + ".asmdef"),
                packageId + ":" + assemblyName);
            File.WriteAllText(Path.Combine(root, "Editor", className + ".cs"),
                "#nullable enable\n" +
                "using System.Collections.Generic;\n" +
                "using OpWeave.Core;\n" +
                "namespace " + packageId + "\n" +
                "{\n" +
                "    [OpWeaveTypeProvider]\n" +
                "    public sealed class " + className + " : ITypeProvider\n" +
                "    {\n" +
                "        public IEnumerable<TypeDefinition> GetTypes() => System.Array.Empty<TypeDefinition>();\n" +
                "    }\n" +
                "}\n");
            WriteAssetMeta(Path.Combine(root, "Editor", className + ".cs"),
                packageId + ":" + className);
        }

        // Deterministic 32-hex GUID for a fixture asset's .meta file —
        // stable across machines so the fixture repos stay identical.
        private static string FixtureGuid(string seed)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(seed));
                var text = new StringBuilder(32);
                foreach (var b in hash)
                {
                    text.Append(b.ToString("x2"));
                }
                return text.ToString();
            }
        }

        // Writes <assetPath>.meta with a deterministic GUID. Folders get
        // the folderAsset form; .cs assets keep the minimal form Unity
        // writes; every other importer block mirrors the committed
        // TestProject fixtures.
        private static void WriteAssetMeta(string assetPath, string seed, bool folder = false)
        {
            var importer =
                Path.GetFileName(assetPath) == "package.json" ? "PackageManifestImporter" :
                assetPath.EndsWith(".asmdef", StringComparison.Ordinal) ? "AssemblyDefinitionImporter" :
                assetPath.EndsWith(".cs", StringComparison.Ordinal) ? "" :
                "TextScriptImporter";
            var body = "";
            if (folder)
            {
                body = "folderAsset: yes\n" + ImporterBody("DefaultImporter");
            }
            else if (importer.Length > 0)
            {
                body = ImporterBody(importer);
            }
            File.WriteAllText(assetPath + ".meta",
                "fileFormatVersion: 2\n" +
                "guid: " + FixtureGuid(seed) + "\n" +
                body);

            static string ImporterBody(string name) =>
                name + ":\n" +
                "  externalObjects: {}\n" +
                "  userData: \n" +
                "  assetBundleName: \n" +
                "  assetBundleVariant: \n";
        }

        // Deletes a fixture tree; git object and pack files are read-only
        // on Windows, so attributes are cleared first.
        private static void DeleteTree(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, true);
        }

        // Idempotent: creates the working + bare repos once per machine and
        // (re)points the tag at HEAD. Returns the fixture's commit SHA —
        // callers keep it in a local or re-derive it (statics and locals
        // die with the domain reload boundary).
        private static string EnsureFixtures()
        {
            Directory.CreateDirectory(FixtureRoot);
            // Fixtures written before the .meta files were added cannot
            // produce a compilable assembly — rebuild a stale layout once.
            var tagPackageMeta = Path.Combine(SrcRepo, "pkgs", "gittag", "package.json.meta");
            if (Directory.Exists(SrcRepo) && !File.Exists(tagPackageMeta))
            {
                DeleteTree(SrcRepo);
                DeleteTree(BareRepo);
            }
            if (!Directory.Exists(SrcRepo))
            {
                WriteFixturePackage(Path.Combine(SrcRepo, "pkgs", "gittag"),
                    TagPackage, TagAssemblyName, "GitTagProvider", FakeTagRepo);
                WriteFixturePackage(Path.Combine(SrcRepo, "pkgs", "gitsha"),
                    ShaPackage, ShaAssemblyName, "GitShaProvider", FakeShaRepo);
                AssertGit(Git("init -b main", SrcRepo), "init");
                AssertGit(Git("add -A", SrcRepo), "add");
                AssertGit(Git(
                    "-c user.name=OpWeaveProvTest -c user.email=prov@opweave.invalid commit -m fixture",
                    SrcRepo), "commit");
            }
            if (!Directory.Exists(BareRepo))
            {
                AssertGit(Git("clone --bare \"" + SrcRepo + "\" \"" + BareRepo + "\"", FixtureRoot),
                    "clone --bare");
            }
            var head = Git("rev-parse HEAD", BareRepo);
            AssertGit(head, "rev-parse HEAD");
            var commit = head.StdOut.Trim();
            Assert.That(commit, Does.Match("^[0-9a-f]{40}$"), "fixture commit SHA unavailable");
            AssertGit(Git("tag -f " + TagName + " " + commit, BareRepo), "tag -f " + TagName);
            return commit;
        }

        // ---------- manifest / package helpers (synchronous only) ----------

        private static readonly Regex s_depsBlock =
            new Regex("\"dependencies\"\\s*:\\s*\\{(?<body>[^{}]*)\\}");
        private static readonly Regex s_depPair =
            new Regex("\"(?<n>[^\"]+)\"\\s*:\\s*\"(?<v>[^\"]*)\"");

        private static List<KeyValuePair<string, string>> ParseDeps(string manifestText)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            var match = s_depsBlock.Match(manifestText);
            Assert.That(match.Success, Is.True, "manifest.json has no \"dependencies\" object");
            foreach (Match pair in s_depPair.Matches(match.Groups["body"].Value))
            {
                pairs.Add(new KeyValuePair<string, string>(pair.Groups["n"].Value, pair.Groups["v"].Value));
            }
            return pairs;
        }

        private static string RebuildManifest(string manifestText, List<KeyValuePair<string, string>> pairs)
        {
            var match = s_depsBlock.Match(manifestText);
            var body = string.Join(",\n", pairs.Select(p => "    \"" + p.Key + "\": \"" + p.Value + "\""));
            return manifestText.Substring(0, match.Index) +
                "\"dependencies\": {\n" + body + "\n  }" +
                manifestText.Substring(match.Index + match.Length);
        }

        private static string SetManifestDependency(string manifestText, string name, string value)
        {
            var pairs = ParseDeps(manifestText).Where(p => p.Key != name).ToList();
            pairs.Add(new KeyValuePair<string, string>(name, value));
            return RebuildManifest(manifestText, pairs);
        }

        private static string StripFixtureDependencies(string manifestText)
        {
            var pairs = ParseDeps(manifestText)
                .Where(p => !p.Key.StartsWith(FixturePrefix, StringComparison.Ordinal))
                .ToList();
            return RebuildManifest(manifestText, pairs);
        }

        private static bool AssemblyLoaded(string name) =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == name);

        private static bool PackageInstalled(string name)
        {
            try
            {
                return UnityEditor.PackageManager.PackageInfo.FindForPackageName(name) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Restores the project baseline synchronously: leftovers removed
        // (Client.Remove needs the dependency still present in the
        // manifest), fixture dependencies stripped, then resolved. The
        // resolve runs only when something was actually removed or
        // rewritten — on a clean baseline a Client.Resolve() reconciles
        // nothing yet still costs a full UPM pass per test. No yields —
        // callers handle the compile/reload boundary themselves.
        private static void RemoveFixturePackages()
        {
            var changed = false;
            if (PackageInstalled(TagPackage))
            {
                try { _ = Client.Remove(TagPackage); } catch (Exception) { /* resolve reconciles */ }
                changed = true;
            }
            if (PackageInstalled(ShaPackage))
            {
                try { _ = Client.Remove(ShaPackage); } catch (Exception) { /* resolve reconciles */ }
                changed = true;
            }
            var manifest = File.ReadAllText(ManifestPath);
            var stripped = StripFixtureDependencies(manifest);
            if (stripped != manifest)
            {
                File.WriteAllText(ManifestPath, stripped);
                changed = true;
            }
            if (changed)
            {
                Client.Resolve();
            }
        }

        private static bool FixtureBaselineAbsent() =>
            !PackageInstalled(TagPackage) && !PackageInstalled(ShaPackage) &&
            !AssemblyLoaded(TagAssemblyName) && !AssemblyLoaded(ShaAssemblyName);

        // ---------- provenance assertions ----------

        private static void AssertProvenanceNeverSelfDeclared(UpmPackageProvenance provenance)
        {
            Assert.That(provenance.ResolvedRevision, Is.Not.EqualTo(FakeRevision));
            Assert.That(provenance.RequestedRevision, Is.Not.EqualTo(FakeRevision));
            if (provenance.SourceRepository is not null)
            {
                Assert.That(provenance.SourceRepository, Does.Not.Contain("self-declared.invalid"),
                    "provenance must come from the UPM manifest/lock resolution, not declared metadata");
            }
        }

        private static void AssertAssemblyProvenance(
            string assemblyName, string packageId, string expectedCommit,
            string expectedRequested, string fakeRepoMarker)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName);
            Assert.That(assembly, Is.Not.Null, $"fixture assembly {assemblyName} is not loaded");

            var result = EditorAssemblyResolver.Resolve(assembly!);
            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var package = result.Value!.Package;
            Assert.That(package, Is.Not.Null, "no package provenance recorded");
            Assert.That(package!.PackageId, Is.EqualTo(packageId));
            Assert.That(package.Source, Is.EqualTo(UpmPackageSource.Git));
            Assert.That(package.ResolvedRevision, Is.EqualTo(expectedCommit),
                "the resolved full commit SHA must be recorded");
            Assert.That(package.RequestedRevision, Is.EqualTo(expectedRequested));
            Assert.That(package.SourceRepository, Is.Not.Null);
            Assert.That(package.SourceRepository,
                Does.StartWith("file:///").And.Contain("provenance-repo"));
            Assert.That(package.SourceRepository, Does.Not.Contain(fakeRepoMarker));
            AssertProvenanceNeverSelfDeclared(package);
            Assert.That(result.Value.PackageId, Is.EqualTo(packageId));
            Assert.That(result.Value.Origin, Is.EqualTo(OperationOrigin.Custom));
        }

        private static void AssertOutcomeProvenance(
            ExtensionDiscoveryReport report, string packageId, string expectedCommit)
        {
            var outcome = report.Extensions.FirstOrDefault(e => e.PackageId == packageId);
            Assert.That(outcome, Is.Not.Null, $"no discovery outcome for {packageId}");
            Assert.That(outcome!.Accepted, Is.True,
                () => string.Join("; ", outcome.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var package = outcome.Package;
            Assert.That(package, Is.Not.Null,
                "discovery outcome carries no package provenance for " + packageId);
            Assert.That(package!.PackageId, Is.EqualTo(packageId));
            Assert.That(package.Source, Is.EqualTo(UpmPackageSource.Git));
            Assert.That(package.ResolvedRevision, Is.EqualTo(expectedCommit));
            AssertProvenanceNeverSelfDeclared(package);
        }

        // The RegistrySnapshot-representing surface (SPEC §C3): the same
        // provenance reachable from the report's Packages map — the lookup
        // describe_operation and the Registry tab use, not only the
        // per-outcome records.
        private static void AssertSnapshotProvenance(
            ExtensionDiscoveryReport report, string packageId, string expectedCommit)
        {
            Assert.That(report.Packages.TryGetValue(packageId, out var package), Is.True,
                $"the snapshot's package info slot carries no provenance for {packageId}");
            Assert.That(package!.PackageId, Is.EqualTo(packageId));
            Assert.That(package.Source, Is.EqualTo(UpmPackageSource.Git));
            Assert.That(package.ResolvedRevision, Is.EqualTo(expectedCommit),
                "the resolved full commit SHA must be reachable from the snapshot");
            AssertProvenanceNeverSelfDeclared(package);
        }

        // The RegistrySnapshot's per-item provenance lookups (SPEC §C3),
        // keyed the way T-W2.2-07 embeds them: the bound Provider ID of an
        // installed Git fixture resolves to the identical provenance
        // record the Packages map carries — same UPM resolution, never
        // self-declared fields.
        private static void AssertItemProvenance(
            ExtensionDiscoveryReport report, string providerId, string packageId,
            string expectedCommit, string expectedRequested)
        {
            Assert.That(report.TypeProviderProvenance.TryGetValue(providerId, out var package),
                Is.True, $"the Provider provenance lookup carries no entry for {providerId}");
            Assert.That(package!.PackageId, Is.EqualTo(packageId));
            Assert.That(package.Source, Is.EqualTo(UpmPackageSource.Git));
            Assert.That(package.ResolvedRevision, Is.EqualTo(expectedCommit),
                "the resolved full commit SHA must be reachable from the per-item lookup");
            Assert.That(package.RequestedRevision, Is.EqualTo(expectedRequested));
            AssertProvenanceNeverSelfDeclared(package);
        }

        // The observed facts the environment actually contains for the
        // valid fixture (T-W2.12-06): its [OpWeaveOperation] class yields
        // the com.opweave.fixture.valid.op@1 Descriptor and its provider
        // offers com.opweave.fixture.valid.type — the set the
        // registration stage supplies through the same seams
        // (SPEC §C3 steps 3-4). A caller that did not gather observations
        // must not claim an empty set: the declared operations[] is
        // compared for exact equality against what is observed
        // (SPEC §X.3.2), so a missing observation is a mismatch.
        private static IReadOnlyList<ObservedExtensionOperation> ValidFixtureObservedOperations() =>
            new[]
            {
                new ObservedExtensionOperation(
                    "com.opweave.fixture.valid.op", 1, ValidEmbeddedAssembly),
            };

        private static IReadOnlyList<ObservedExtensionType> ValidFixtureObservedTypes() =>
            new[]
            {
                new ObservedExtensionType(
                    "com.opweave.fixture.valid.type", ValidEmbeddedAssembly),
            };

        // For every extension verdict in the report: each item an
        // accepted extension contributes (bound Provider IDs and the
        // declared operations[] identities that equal its discovered
        // Descriptor set, SPEC §X.3.2) resolves in the keyed lookups to
        // the same provenance record the Packages map carries; no item a
        // rejected extension declared resolves to anything.
        private static void AssertItemProvenanceConsistency(ExtensionDiscoveryReport report)
        {
            foreach (var outcome in report.Extensions)
            {
                if (!outcome.Accepted)
                {
                    if (outcome.Manifest is null)
                    {
                        continue;
                    }
                    foreach (var operation in outcome.Manifest.Operations)
                    {
                        Assert.That(report.OperationProvenance.ContainsKey(operation), Is.False,
                            $"rejected extension {outcome.PackageId} must contribute no Operation provenance");
                    }
                    foreach (var provider in outcome.Manifest.TypeProviders)
                    {
                        Assert.That(report.TypeProviderProvenance.ContainsKey(provider.Id), Is.False,
                            $"rejected extension {outcome.PackageId} must contribute no Provider provenance");
                    }
                    continue;
                }
                Assert.That(outcome.Package, Is.Not.Null,
                    $"accepted outcome {outcome.PackageId} carries no package provenance");
                foreach (var binding in outcome.TypeProviders)
                {
                    Assert.That(
                        report.TypeProviderProvenance.TryGetValue(binding.ProviderId, out var providerPackage),
                        Is.True,
                        $"the Provider provenance lookup misses {binding.ProviderId} of {outcome.PackageId}");
                    Assert.That(providerPackage, Is.SameAs(outcome.Package),
                        "the per-item lookup must return the same record as the package info slot");
                }
                if (outcome.Manifest is null)
                {
                    continue;
                }
                foreach (var operation in outcome.Manifest.Operations)
                {
                    Assert.That(
                        report.OperationProvenance.TryGetValue(operation, out var operationPackage),
                        Is.True,
                        $"the Operation provenance lookup misses ({operation.Id}, {operation.Version}) of {outcome.PackageId}");
                    Assert.That(operationPackage, Is.SameAs(outcome.Package),
                        "the per-item lookup must return the same record as the package info slot");
                }
            }
        }

        // ---------- setUp / tearDown ----------

        // Installing/removing a package makes the Editor emit a benign
        // [Error] log ("Asset Packages/<name>/package.json has no meta
        // file, but it's in an immutable folder") — the same expected
        // byproduct the T-W2.12-05 probe documents.
        [UnitySetUp]
        public IEnumerator UnitySetUp()
        {
            LogAssert.ignoreFailingMessages = true;
            RemoveFixturePackages();

            // A leftover fixture removal may still be importing or
            // compiling; its reload then stays deferred at the test
            // runner's assembly lock — possibly with every public flag
            // already quiet, since the import and compile can finish
            // inside the synchronous Resolve (Unity 6000.0 has no public
            // "reload requested" flag). Wait a short quiet window, then
            // let a still-present fixture prove the deferred state and
            // cross it at the sanctioned boundary.
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var quietStart = -1.0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (FixtureBaselineAbsent())
                {
                    break;
                }
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    quietStart = -1.0;
                }
                else if (quietStart < 0.0)
                {
                    quietStart = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - quietStart >= QuietSettleSec)
                {
                    break;
                }
                yield return null;
            }
            if (!FixtureBaselineAbsent())
            {
                AssetDatabase.Refresh();
                yield return new WaitForDomainReload();
            }
            Assert.That(FixtureBaselineAbsent(), Is.True, "fixture packages still installed");
        }

        [UnityTearDown]
        public IEnumerator UnityTearDown()
        {
            LogAssert.ignoreFailingMessages = true;
            RemoveFixturePackages();

            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var quietStart = -1.0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (FixtureBaselineAbsent())
                {
                    break;
                }
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    quietStart = -1.0;
                }
                else if (quietStart < 0.0)
                {
                    quietStart = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - quietStart >= QuietSettleSec)
                {
                    break;
                }
                yield return null;
            }
            if (!FixtureBaselineAbsent())
            {
                AssetDatabase.Refresh();
                yield return new WaitForDomainReload();
            }
            Assert.That(FixtureBaselineAbsent(), Is.True,
                "project not restored to baseline after provenance fixture cleanup");
        }

        // ---------- local fixtures: embedded / local / project provenance ----------

        [Test]
        public void EmbeddedFixturePackage_RecordsEmbeddedProvenance()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == ValidEmbeddedAssembly)
                ?.GetType(ValidEmbeddedProvider);
            Assert.That(type, Is.Not.Null, "embedded fixture provider type missing");

            var result = EditorAssemblyResolver.ResolveForType(type!);

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var package = result.Value!.Package;
            Assert.That(package, Is.Not.Null);
            Assert.That(package!.PackageId, Is.EqualTo(ValidEmbeddedPackage));
            Assert.That(package.Source, Is.EqualTo(UpmPackageSource.Embedded));
            Assert.That(package.ResolvedRevision, Is.Null);
            Assert.That(package.RequestedRevision, Is.Null);
            Assert.That(package.SourceRepository, Is.Null);
            Assert.That(result.Value.PackageId, Is.EqualTo(ValidEmbeddedPackage));
            Assert.That(result.Value.Origin, Is.EqualTo(OperationOrigin.Custom));
        }

        [Test]
        public void StandardPackage_RecordsLocalProvenance()
        {
            var result = EditorAssemblyResolver.Resolve(typeof(EditorAssemblyResolver).Assembly);

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var package = result.Value!.Package;
            Assert.That(package, Is.Not.Null);
            Assert.That(package!.PackageId, Is.EqualTo(EditorAssemblyResolver.StandardPackageId));
            // The test project references the package as a file: dependency.
            Assert.That(package.Source, Is.Not.EqualTo(UpmPackageSource.Git));
            Assert.That(package.ResolvedRevision, Is.Null);
            Assert.That(package.RequestedRevision, Is.Null);
            Assert.That(result.Value.Origin, Is.EqualTo(OperationOrigin.Standard));
        }

        [Test]
        public void ProjectAssembly_HasNoPackageProvenance()
        {
            var fixture = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "OpWeaveFixtures.Editor");
            Assert.That(fixture, Is.Not.Null, "project fixture assembly missing");

            var result = EditorAssemblyResolver.Resolve(fixture!);

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(result.Value!.Package, Is.Null);
            Assert.That(result.Value.PackageId, Is.Null);
            Assert.That(result.Value.Origin, Is.EqualTo(OperationOrigin.Custom));
        }

        [Test]
        public void ProjectPrecompiledDll_HasNoPackageProvenance()
        {
            // OpWeave.Plugin.dll is an Editor-only precompiled plugin under
            // Assets/: no compilation assembly exists, so the package lookup
            // runs on the DLL's asset path — which is not inside a package.
            var fixture = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "OpWeave.Plugin");
            Assert.That(fixture, Is.Not.Null, "precompiled fixture assembly missing");

            var result = EditorAssemblyResolver.Resolve(fixture!);

            Assert.That(result.Ok, Is.True,
                () => string.Join("; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(result.Value!.IsPrecompiled, Is.True);
            Assert.That(result.Value.Package, Is.Null);
            Assert.That(result.Value.PackageId, Is.Null);
            Assert.That(result.Value.Origin, Is.EqualTo(OperationOrigin.Custom));
        }

        [Test]
        public void DiscoveryOutcome_CarriesEmbeddedPackageProvenance()
        {
            var report = ExtensionDiscovery.Discover(
                ValidFixtureObservedOperations(), ValidFixtureObservedTypes());

            var outcome = report.Extensions
                .FirstOrDefault(e => e.PackageId == ValidEmbeddedPackage);
            Assert.That(outcome, Is.Not.Null, "valid embedded fixture was not discovered");
            Assert.That(outcome!.Accepted, Is.True,
                () => string.Join("; ", outcome.Diagnostics.Select(d => d.Code + " " + d.Message)));
            Assert.That(outcome.Package, Is.Not.Null,
                "discovery outcome carries no package provenance");
            Assert.That(outcome.Package!.PackageId, Is.EqualTo(ValidEmbeddedPackage));
            Assert.That(outcome.Package.Source, Is.EqualTo(UpmPackageSource.Embedded));
            Assert.That(outcome.Package.ResolvedRevision, Is.Null);

            // The same record is reachable from the snapshot's package
            // info slot (SPEC §C3).
            Assert.That(report.Packages.TryGetValue(ValidEmbeddedPackage, out var info), Is.True,
                "the snapshot's package info slot misses the embedded fixture");
            Assert.That(info!.Source, Is.EqualTo(UpmPackageSource.Embedded));
            Assert.That(info.ResolvedRevision, Is.Null);
        }

        // ---------- Git-installed fixtures: resolved commit by tag and by SHA ----------

        // local:T-W2.12-02:upm-provenance — installs the fixture package via
        // Git URL by tag (?path=/pkgs/gittag#vtag1) and by commit
        // (?path=/pkgs/gitsha#<sha>) and inspects the recorded provenance:
        // the resolved full commit SHA is recorded in both cases even though
        // one install requested a tag, and self-declared repository fields
        // in the fixture metadata are never used.
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator GitInstalledPackages_RecordResolvedCommit()
        {
            LogAssert.ignoreFailingMessages = true;

            // Phase 1 (pre-reload): install both fixture packages. Every
            // local here dies at the WaitForDomainReload boundary — the
            // commit SHA is re-derived from the fixture afterwards.
            var commit = EnsureFixtures();
            var manifest = File.ReadAllText(ManifestPath);
            manifest = SetManifestDependency(
                manifest, TagPackage, BareRepoUrl + "?path=/pkgs/gittag#" + TagName);
            manifest = SetManifestDependency(
                manifest, ShaPackage, BareRepoUrl + "?path=/pkgs/gitsha#" + commit);
            File.WriteAllText(ManifestPath, manifest);
            Client.Resolve();

            // The install's import and compile may complete inside the
            // synchronous Resolve while the runner holds the reload lock,
            // leaving every public flag already quiet with the reload
            // still deferred (Unity 6000.0 has no public "reload
            // requested" flag). Wait on observable state instead: UPM
            // registering the packages, then their assemblies being
            // absent until the deferred reload is crossed at the
            // sanctioned boundary. AssetDatabase.Refresh flushes any
            // still-queued import so the request is registered first.
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (PackageInstalled(TagPackage) && PackageInstalled(ShaPackage))
                {
                    break;
                }
                yield return null;
            }
            Assert.That(PackageInstalled(TagPackage) && PackageInstalled(ShaPackage), Is.True,
                "the fixture packages never registered with UPM after the manifest install");
            if (!AssemblyLoaded(TagAssemblyName) || !AssemblyLoaded(ShaAssemblyName))
            {
                AssetDatabase.Refresh();
                yield return new WaitForDomainReload();
            }

            // Phase 2 (post-reload): all state re-derived — the fixture
            // commit comes back out of the bare repo. An in-flight compile
            // always ends in a domain reload, crossed at the same
            // boundary; a deferred reload invisible to the public flags is
            // flushed the same way after a quiet settle.
            var loadedDeadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var quietStart = -1.0;
            while (EditorApplication.timeSinceStartup < loadedDeadline)
            {
                if (EditorApplication.isCompiling)
                {
                    yield return new WaitForDomainReload();
                    loadedDeadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
                    quietStart = -1.0;
                    continue;
                }
                if (EditorApplication.isUpdating)
                {
                    quietStart = -1.0;
                    yield return null;
                    continue;
                }
                // Flags quiet: either the assemblies are loaded and the
                // work is done, or a deferred reload is pending invisibly.
                // Loaded assemblies already prove the install's reload
                // completed (they can only load inside the new domain),
                // so the quiet settle window then detects nothing.
                if (AssemblyLoaded(TagAssemblyName) && AssemblyLoaded(ShaAssemblyName))
                {
                    break;
                }
                if (quietStart < 0.0)
                {
                    quietStart = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - quietStart >= QuietSettleSec)
                {
                    break;
                }
                yield return null;
            }
            if (!AssemblyLoaded(TagAssemblyName) || !AssemblyLoaded(ShaAssemblyName))
            {
                AssetDatabase.Refresh();
                yield return new WaitForDomainReload();
            }
            Assert.That(AssemblyLoaded(TagAssemblyName), Is.True,
                "tag fixture assembly never loaded after the domain reload");
            Assert.That(AssemblyLoaded(ShaAssemblyName), Is.True,
                "sha fixture assembly never loaded after the domain reload");
            commit = Git("rev-parse " + TagName, BareRepo).StdOut.Trim();
            Assert.That(commit, Does.Match("^[0-9a-f]{40}$"));

            // By tag: the requested revision is the tag text, the recorded
            // resolved revision is the full commit SHA the lock pinned.
            AssertAssemblyProvenance(
                TagAssemblyName, TagPackage, commit, TagName, "fake-tag");
            // By commit: requested and resolved are the same full SHA.
            AssertAssemblyProvenance(
                ShaAssemblyName, ShaPackage, commit, commit, "fake-sha");

            // The same provenance reaches the discovery outcome and the
            // report's Packages map — the package info slot of the
            // RegistrySnapshot representation (SPEC §C3), verified here
            // through the snapshot for both the tag and the commit fixture.
            var report = ExtensionDiscovery.Discover(
                ValidFixtureObservedOperations(), ValidFixtureObservedTypes());
            AssertOutcomeProvenance(report, TagPackage, commit);
            AssertOutcomeProvenance(report, ShaPackage, commit);
            AssertSnapshotProvenance(report, TagPackage, commit);
            AssertSnapshotProvenance(report, ShaPackage, commit);

            // Each fixture's bound Provider ID resolves through the
            // report's keyed per-item lookup — the form T-W2.2-07 embeds
            // into RegistrySnapshot — to the identical Git provenance.
            AssertItemProvenance(report, TagPackage + ".types", TagPackage, commit, TagName);
            AssertItemProvenance(report, ShaPackage + ".types", ShaPackage, commit, commit);
            AssertItemProvenanceConsistency(report);
        }

        // ---------- keyed per-item provenance lookups (SPEC §C3) ----------

        // local:T-W2.12-02:upm-provenance — the per-Operation (id, version)
        // and per-TypeProvider (Provider ID) provenance lookups the
        // Registry build consumes from this discovery/registration result
        // (the keyed form T-W2.2-07 embeds into RegistrySnapshot): every
        // item an accepted installed extension contributes resolves to
        // that extension's UPM-resolved provenance; every item a rejected
        // extension declared resolves to none.
        [Test]
        public void ItemProvenanceLookups_AcceptedItemsResolve_RejectedReturnNone()
        {
            var report = ExtensionDiscovery.Discover(
                ValidFixtureObservedOperations(), ValidFixtureObservedTypes());
            AssertItemProvenanceConsistency(report);

            // The valid embedded fixture's bound Provider ID resolves to
            // the same embedded provenance record as the package info slot.
            var valid = report.Extensions.First(e => e.PackageId == ValidEmbeddedPackage);
            Assert.That(report.TypeProviderProvenance.TryGetValue(
                "com.opweave.fixture.valid.types", out var providerPackage), Is.True,
                "the Provider provenance lookup misses the valid fixture's bound Provider ID");
            Assert.That(providerPackage, Is.SameAs(valid.Package));
            Assert.That(providerPackage!.PackageId, Is.EqualTo(ValidEmbeddedPackage));
            Assert.That(providerPackage.Source, Is.EqualTo(UpmPackageSource.Embedded));
            Assert.That(providerPackage.ResolvedRevision, Is.Null);
        }

        // SPEC §X.3.2 fail closed on a real observed mismatch: a supplied
        // observed Descriptor set that does not equal the declared
        // operations[] rejects the whole extension, so its declared items
        // resolve to no provenance. Supplying no observations claims an
        // empty observed set, which mismatches the valid fixture's
        // declared Operation the same way — "not observed" and "observed
        // and mismatched" both reject; only the observed facts the
        // environment actually contains accept it.
        [Test]
        public void ItemProvenanceLookups_ObservedMismatchRejectsExtension()
        {
            var mismatched = ExtensionDiscovery.Discover(
                new[]
                {
                    new ObservedExtensionOperation(
                        "com.opweave.fixture.valid.op", 2, ValidEmbeddedAssembly),
                },
                ValidFixtureObservedTypes());
            var outcome = mismatched.Extensions.First(e => e.PackageId == ValidEmbeddedPackage);
            Assert.That(outcome.Accepted, Is.False,
                "an observed Descriptor set differing from the declared operations[] must reject");
            Assert.That(outcome.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("does not match"));
            Assert.That(mismatched.OperationProvenance.ContainsKey(
                new ExtensionOperationRef("com.opweave.fixture.valid.op", 1)), Is.False,
                "a rejected extension contributes no Operation provenance entry");
            Assert.That(mismatched.TypeProviderProvenance.ContainsKey(
                "com.opweave.fixture.valid.types"), Is.False,
                "a rejected extension contributes no Provider provenance entry");

            var unobserved = ExtensionDiscovery.Discover();
            var unobservedOutcome =
                unobserved.Extensions.First(e => e.PackageId == ValidEmbeddedPackage);
            Assert.That(unobservedOutcome.Accepted, Is.False,
                "an empty observed set mismatches the declared Operation identically");
            Assert.That(unobservedOutcome.Diagnostics.Select(d => d.Message),
                Has.Some.Contains("does not match"));
            Assert.That(unobserved.TypeProviderProvenance.ContainsKey(
                "com.opweave.fixture.valid.types"), Is.False);
        }

        // The live report's accepted Operation key — the valid fixture's
        // declared-and-observed com.opweave.fixture.valid.op@1 — is
        // exercised through AssertItemProvenanceConsistency above; this
        // synthetic report adds what the live project cannot cover: a
        // second version of an accepted Operation ID missing the key, a
        // rejected extension's declared items and the lookups' read-only
        // contract, assembled the way the Registry build consumes a
        // report.
        [Test]
        public void ItemProvenanceLookups_OperationKeysResolve_AndLookupsAreReadOnly()
        {
            var acceptedPackage = new UpmPackageProvenance(
                "com.example.ext", "1.2.3", UpmPackageSource.Git,
                "0123456789abcdef0123456789abcdef01234567", "v9",
                "https://example.invalid/ext.git");
            var rejectedPackage = new UpmPackageProvenance(
                "com.example.bad", "9.9.9", UpmPackageSource.Local, null, null, null);
            var accepted = ExtensionValidationOutcome.Accept(
                    "com.example.ext",
                    new ExtensionManifest(
                        "1.0", "com.example.ext", null,
                        new CoreApiRange(new CoreApiVersion(1, 0, 0), new CoreApiVersion(2, 0, 0)),
                        "6000.0",
                        new[] { "Example.Ext.Editor" },
                        new[] { new ExtensionOperationRef("com.example.ext.op", 2) },
                        new[] { new ExtensionTypeProviderRef("com.example.ext.types", "Example.Ext.Editor") },
                        Array.Empty<string>(), Array.Empty<string>()),
                    new[] { new TypeProviderBinding(
                        "com.example.ext.types", "com.example.ext.Provider", "Example.Ext.Editor") })
                .WithPackage(acceptedPackage);
            var rejected = ExtensionValidationOutcome.Reject(
                    "com.example.bad",
                    manifestPresent: true,
                    manifest: new ExtensionManifest(
                        "1.0", "com.example.bad", null,
                        new CoreApiRange(new CoreApiVersion(1, 0, 0), new CoreApiVersion(2, 0, 0)),
                        "6000.0",
                        new[] { "Example.Bad.Editor" },
                        new[] { new ExtensionOperationRef("com.example.bad.op", 1) },
                        new[] { new ExtensionTypeProviderRef("com.example.bad.types", "Example.Bad.Editor") },
                        Array.Empty<string>(), Array.Empty<string>()),
                    diagnostics: new[] { new Diagnostic(string.Empty, Severity.Error, "rejected") })
                .WithPackage(rejectedPackage);

            var report = new ExtensionDiscoveryReport(
                new[] { accepted, rejected },
                Array.Empty<DiscoveredTypeProvider>(),
                Array.Empty<UnattributedExtensionCandidate>(),
                new[]
                {
                    new KeyValuePair<string, UpmPackageProvenance>("com.example.ext", acceptedPackage),
                    new KeyValuePair<string, UpmPackageProvenance>("com.example.bad", rejectedPackage),
                });

            // The accepted extension's items resolve to its provenance:
            // Package ID, package version, source kind and the resolved
            // full commit SHA.
            Assert.That(report.OperationProvenance.TryGetValue(
                new ExtensionOperationRef("com.example.ext.op", 2), out var operationPackage), Is.True,
                "the Operation provenance lookup misses the accepted (id, version)");
            Assert.That(operationPackage, Is.SameAs(acceptedPackage));
            Assert.That(operationPackage!.PackageId, Is.EqualTo("com.example.ext"));
            Assert.That(operationPackage.Version, Is.EqualTo("1.2.3"));
            Assert.That(operationPackage.Source, Is.EqualTo(UpmPackageSource.Git));
            Assert.That(operationPackage.ResolvedRevision,
                Is.EqualTo("0123456789abcdef0123456789abcdef01234567"));
            Assert.That(report.TypeProviderProvenance.TryGetValue(
                "com.example.ext.types", out var providerPackage), Is.True,
                "the Provider provenance lookup misses the accepted Provider ID");
            Assert.That(providerPackage, Is.SameAs(acceptedPackage));

            // The key is (id, version): another version of the same id
            // misses, and the rejected extension's declared items resolve
            // to none.
            Assert.That(report.OperationProvenance.ContainsKey(
                new ExtensionOperationRef("com.example.ext.op", 1)), Is.False);
            Assert.That(report.OperationProvenance.ContainsKey(
                new ExtensionOperationRef("com.example.bad.op", 1)), Is.False);
            Assert.That(report.TypeProviderProvenance.ContainsKey(
                "com.example.bad.types"), Is.False);

            // The lookups are immutable snapshot views.
            Assert.Throws<NotSupportedException>(() =>
                ((IDictionary<ExtensionOperationRef, UpmPackageProvenance>)report.OperationProvenance)
                    .Add(new ExtensionOperationRef("com.example.new.op", 1), acceptedPackage));
            Assert.Throws<NotSupportedException>(() =>
                ((IDictionary<string, UpmPackageProvenance>)report.TypeProviderProvenance)
                    .Add("com.example.new.types", acceptedPackage));
        }
    }
}
