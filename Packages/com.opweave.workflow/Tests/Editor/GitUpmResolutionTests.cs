#nullable enable

// T-W3.8-01 [Edit] tests — local:T-W3.8-01:git-upm-resolution — the G-06
// release gate's UPM half (SPEC §T.1 G-06, §X.3, §X.4): prove that the
// manifest/lock resolution of the two real, independent Git remotes —
// https://github.com/pippirium/OpWeave-dev.git carrying
// com.opweave.workflow at ?path=/Packages/com.opweave.workflow and
// https://github.com/pippirium/OpWeave-Sample-Operations.git carrying
// com.opweave.sample.operations — pins full commit SHAs, and that the
// SAME commits reach the Registry's per-Operation provenance slots
// (SPEC §C3 / §X.3 package info: RegisteredOperation.PackageId and
// ResolvedCommit). No local folder, moving tag or fabricated SHA is ever
// substituted.
//
// Design: the check runs on a DISPOSABLE child project under
// TestProject/Library/OpWeaveG06Probe/ (Library is not versioned). The
// ticket's scope names "a TestProject configuration referencing" the Git
// URLs; that configuration is materialised as the child's own
// Packages/manifest.json — the shared TestProject manifest is never
// modified, so the check cannot destabilise the rest of the EditMode
// suite (which is what exclusive_resources on
// TestProject/Packages/manifest.json protects). The child gets a probe
// assembly Assets/OpWeaveG06Probe/OpWeave.G06Probe.asmdef into which this
// test copies GitUpmResolutionEntry.cs; a batchmode child Editor spawned
// by EditorProcessHarness then runs the real §C3 registry build there and
// writes verdict.json: the lock entry, the UPM PackageInfo record and the
// registry provenance per package.
//
// The probe name is deliberately NOT an InternalsVisibleTo name: the
// Git-installed com.opweave.workflow ships its own asmdefs under
// Packages/com.opweave.workflow/ — including
// Tests/Editor/OpWeave.UnityAdapter.Tests.asmdef — so a probe asmdef
// carrying any granted name duplicates the package's own assembly
// ("Assembly with name 'X' already exists", a hard compile error). Every
// granted name collides, so the entry reaches the internal
// RegistryBuildHost through reflection instead (see
// GitUpmResolutionEntry.cs) — public API carries no snapshot read seam.
//
// Two cases satisfy the acceptance criteria:
//   pinned   — both remotes referenced at a full 40-hex commit SHA; the
//              lock's hash, PackageInfo.git.hash and every registered
//              Operation's ResolvedCommit must equal the pinned SHAs.
//   unpinned — the same dependencies without a #revision (HEAD); the
//              resolved commit must still be recorded consistently by all
//              three views (its value is whatever HEAD resolves to at
//              run time — only the 40-hex shape and the three-way match
//              are asserted, so a moving main cannot flake the test).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.G06Probe;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W3.8-01:git-upm-resolution</c> — Git UPM manifest/lock
    /// resolution and Registry provenance for the two independent OpWeave
    /// remotes (SPEC §T.1 G-06).
    /// </summary>
    internal sealed class GitUpmResolutionTests
    {
        private const string WorkflowPackageId = "com.opweave.workflow";
        private const string SamplePackageId = "com.opweave.sample.operations";

        private const string WorkflowGitRepo = "https://github.com/pippirium/OpWeave-dev.git";
        private const string WorkflowGitPath = "/Packages/com.opweave.workflow";
        private const string SampleGitRepo = "https://github.com/pippirium/OpWeave-Sample-Operations.git";

        // Full commit SHAs the Coordinator verified on both remotes with
        // git ls-remote (the worker has no network access of its own):
        // the sample revision on 2026-11-20, the OpWeave-dev revision on
        // 2026-11-21. OpWeave-dev is pinned to 5fc373b2, a main merge
        // commit after the missing-.meta maintenance PR (merged at main
        // 4b55ba5e): the tree is meta-complete, so it compiles as an
        // immutable Git package, and it carries the
        // OpWeaveOperationAttribute(id, version) API that the pinned
        // sample revision 92865779 requires (the older pin predates that
        // constructor and fails CS1729 in the child Editor).
        private const string WorkflowPinnedSha = "5fc373b2621281c668fa2757c9c6a7056f6ceef3";
        private const string SamplePinnedSha = "92865779755002c4b928a2d788d55dceddc99c86";

        // Unique by necessity — see the header comment: every
        // InternalsVisibleTo-granted name is also an asmdef name shipped
        // inside the Git-installed package and would collide at asmdef
        // scan.
        private const string ProbeAssemblyName = "OpWeave.G06Probe";
        private const string ProbeFolderName = "OpWeaveG06Probe";
        private const string Entry =
            ProbeAssemblyName + ".GitUpmResolutionEntry.Run";

        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveG06Probe");

        private static string ChildProjectDir => Path.Combine(ProbeDir, "project");
        private static string JobsDir => Path.Combine(ProbeDir, "jobs");

        // The child's first launch resolves two Git remotes and builds a
        // whole Library; later launches reuse it. A hang is a failure, not
        // a flake to retry — the bounds stay generous on purpose.
        private const int LaunchTimeoutMs = 25 * 60 * 1000;
        private const int ChildStopMs = 30000;

        private static readonly System.Text.Encoding s_utf8 =
            new System.Text.UTF8Encoding(false);

        private readonly EditorProcessHarness _harness = new EditorProcessHarness();
        private readonly List<EditorProcessHarness.EditorChild> _children =
            new List<EditorProcessHarness.EditorChild>();

        // ---------- child project skeleton ----------

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (Directory.Exists(ProbeDir))
            {
                try { Directory.Delete(ProbeDir, recursive: true); }
                catch (Exception) { /* leftover handles stay for the next run */ }
            }
            // The ProjectSettings clone keeps the child's editor version,
            // quality tags and any OpWeave settings aligned with the
            // tested project — Assets/ and Packages/ are built below, the
            // Library/ the child creates on first open.
            CopyDirectory(
                Path.Combine(ProjectRoot, "ProjectSettings"),
                Path.Combine(ChildProjectDir, "ProjectSettings"));
            EnsureProbeAssembly();
        }

        [SetUp]
        public void SetUp() => EnsureProbeAssembly();

        [TearDown]
        public void TearDown()
        {
            foreach (var child in _children)
            {
                try
                {
                    if (child.Process.HasExited)
                    {
                        continue;
                    }
                    if (!child.Process.WaitForExit(ChildStopMs))
                    {
                        child.Process.Kill();
                        child.Process.WaitForExit(ChildStopMs);
                    }
                }
                catch (Exception)
                {
                    // Best effort: a leaked child dies with the test Editor.
                }
                finally
                {
                    child.Process.Dispose();
                }
            }
            _children.Clear();
        }

        // The probe assembly the child's -executeMethod lives in: a
        // unique name nothing in the Git-installed package or the sample
        // ships (every InternalsVisibleTo name collides — see the header
        // comment), so the entry reads the registry build through
        // reflection — without defineConstraints or testables, because the
        // child never runs the test framework. GitUpmResolutionEntry.cs is
        // copied verbatim from this checkout so one file compiles in both
        // domains — including against the pinned package's older API
        // surface.
        private static void EnsureProbeAssembly()
        {
            var probeDir = Path.Combine(ChildProjectDir, "Assets", ProbeFolderName);
            Directory.CreateDirectory(probeDir);
            File.WriteAllText(
                Path.Combine(probeDir, ProbeAssemblyName + ".asmdef"),
                "{\n" +
                "    \"name\": \"" + ProbeAssemblyName + "\",\n" +
                "    \"rootNamespace\": \"" + ProbeAssemblyName + "\",\n" +
                "    \"references\": [\n" +
                "        \"OpWeave.Core\",\n" +
                "        \"OpWeave.UnityAdapter\"\n" +
                "    ],\n" +
                "    \"includePlatforms\": [\n" +
                "        \"Editor\"\n" +
                "    ],\n" +
                "    \"excludePlatforms\": [],\n" +
                "    \"allowUnsafeCode\": false,\n" +
                "    \"overrideReferences\": true,\n" +
                "    \"precompiledReferences\": [\n" +
                "        \"Newtonsoft.Json.dll\"\n" +
                "    ],\n" +
                "    \"autoReferenced\": true,\n" +
                "    \"defineConstraints\": [],\n" +
                "    \"versionDefines\": [],\n" +
                "    \"noEngineReferences\": false\n" +
                "}\n",
                s_utf8);
            var entrySource = GitUpmResolutionEntry.SourcePath();
            Assert.That(File.Exists(entrySource), Is.True,
                "the entry-point source file is missing: " + entrySource);
            Assert.That(Path.GetFileName(entrySource),
                Is.EqualTo("GitUpmResolutionEntry.cs"),
                "SourcePath() must resolve this file's own path, not a " +
                "caller's — copying anything else into the probe assembly " +
                "fails the child's compile");
            File.Copy(
                entrySource,
                Path.Combine(probeDir, "GitUpmResolutionEntry.cs"),
                overwrite: true);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(
                    file,
                    Path.Combine(destination, Path.GetFileName(file)),
                    overwrite: true);
            }
            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(
                    directory,
                    Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        // ---------- child manifest ----------

        // The module set the tested project uses; the child's manifest
        // keeps exactly those plus the two Git dependencies — nothing else
        // a full OpWeave compile needs.
        private static IReadOnlyList<KeyValuePair<string, string>> ModuleDependencies()
        {
            var manifest = JObject.Parse(
                File.ReadAllText(Path.Combine(ProjectRoot, "Packages", "manifest.json")));
            var dependencies = manifest["dependencies"] as JObject ?? new JObject();
            return dependencies.Properties()
                .Where(p => p.Name.StartsWith("com.unity.modules.", StringComparison.Ordinal))
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.ToString()))
                .ToList();
        }

        private static void WriteManifest(string workflowDep, string sampleDep)
        {
            var dependencies = new JObject
            {
                [WorkflowPackageId] = workflowDep,
                [SamplePackageId] = sampleDep,
            };
            foreach (var module in ModuleDependencies())
            {
                dependencies[module.Key] = module.Value;
            }
            var packagesDir = Path.Combine(ChildProjectDir, "Packages");
            Directory.CreateDirectory(packagesDir);
            File.WriteAllText(
                Path.Combine(packagesDir, "manifest.json"),
                new JObject { ["dependencies"] = dependencies }.ToString(),
                s_utf8);
            // A rewritten manifest re-resolves on the next project open;
            // removing the stale lock first makes it impossible for the
            // verdict to describe a previous case's resolution.
            var lockPath = Path.Combine(packagesDir, "packages-lock.json");
            if (File.Exists(lockPath))
            {
                File.Delete(lockPath);
            }
            var unityLock = Path.Combine(ChildProjectDir, "Temp", "UnityLockfile");
            if (File.Exists(unityLock))
            {
                try { File.Delete(unityLock); }
                catch (Exception) { /* the child reports a lock it cannot clear */ }
            }
        }

        // ---------- child launch / verdict ----------

        private JObject RunCase(string caseName, string workflowDep, string sampleDep)
        {
            WriteManifest(workflowDep, sampleDep);
            var jobDir = Path.Combine(JobsDir, caseName);
            Directory.CreateDirectory(jobDir);
            var environment = new Dictionary<string, string>
            {
                [EditorProcessHarness.JobDirEnvVar] = jobDir,
            };
            var child = _harness.Launch(
                ChildProjectDir, Entry, Path.Combine(jobDir, "child.log"), environment);
            child.JobDir = jobDir;
            _children.Add(child);
            if (!EditorProcessHarness.WaitForExit(child, LaunchTimeoutMs))
            {
                try { EditorProcessHarness.KillAndWait(child, ChildStopMs); }
                catch (Exception) { /* the assert below reports the hang */ }
                Assert.Fail(
                    "the G-06 child Editor did not exit within " + LaunchTimeoutMs +
                    " ms; log: " + child.LogPath + "; fatal: " + Fatal(jobDir) +
                    "; output: " + EditorProcessHarness.ChildOutput(child));
            }
            Assert.That(child.Process.ExitCode, Is.EqualTo(0),
                "the G-06 child Editor failed; log: " + child.LogPath +
                "; fatal: " + Fatal(jobDir) + "; output: " +
                EditorProcessHarness.ChildOutput(child));
            var text = EditorProcessHarness.WaitForFileContent(
                Path.Combine(jobDir, "verdict.json"), 30000);
            Assert.IsNotNull(text,
                "the G-06 child never wrote verdict.json; log: " + child.LogPath +
                "; fatal: " + Fatal(jobDir));
            return JObject.Parse(text!);
        }

        private static string Fatal(string jobDir)
        {
            var path = Path.Combine(jobDir, "fatal.txt");
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : "<none>";
            }
            catch (Exception)
            {
                return "<unreadable>";
            }
        }

        // ---------- assertions ----------

        private static void AssertVerdictHealth(JObject verdict)
        {
            Assert.That(verdict["compilationConsistent"]?.Value<bool>(), Is.True,
                "the child project's assemblies did not compile consistently");
            Assert.That(verdict["stale"]?.Value<bool>(), Is.False,
                "the child registry build went stale");
            Assert.That(verdict["snapshotPublished"]?.Value<bool>(), Is.True,
                "the child registry build published no snapshot");
        }

        // The G-06 invariant for one package: three independent views of
        // the same UPM resolution — the lock entry, the PackageManager's
        // PackageInfo record and the RegistrySnapshot's per-Operation
        // provenance — must all report one identical full commit SHA.
        private static void AssertPackage(
            JObject verdict, string packageId, string expectedRepo, string? expectedSha)
        {
            var package = verdict["packages"]?[packageId] as JObject;
            Assert.That(package, Is.Not.Null,
                "the verdict carries no package section for " + packageId);

            var lockEntry = package!["lock"] as JObject;
            Assert.That(lockEntry, Is.Not.Null,
                packageId + " is absent from the child's packages-lock.json");
            Assert.That(lockEntry!["source"]?.ToString(), Is.EqualTo("git"),
                packageId + " did not resolve from a Git remote");
            var lockHash = lockEntry["hash"]?.ToString();
            Assert.That(lockHash, Does.Match("^[0-9a-f]{40}$"),
                packageId + ": the lock must record the full resolved commit SHA");
            if (expectedSha is not null)
            {
                Assert.That(lockHash, Is.EqualTo(expectedSha),
                    packageId + ": the lock resolved a different commit than the pinned one");
            }

            var info = package["packageInfo"] as JObject;
            Assert.That(info, Is.Not.Null,
                "the PackageManager reports no " + packageId);
            Assert.That(info!["packageId"]?.ToString(), Does.Contain(expectedRepo),
                packageId + ": provenance must name the real remote, not a substitute");
            Assert.That(info["source"]?.ToString(), Is.EqualTo("Git"),
                packageId + ": PackageInfo source must be Git");
            Assert.That(info["gitHash"]?.ToString(), Is.EqualTo(lockHash),
                packageId + ": PackageInfo.git.hash disagrees with the lock");
            if (expectedSha is not null)
            {
                // §X.3.2: the requested revision is recorded verbatim next
                // to the resolved commit — a pinned request names its SHA.
                Assert.That(info["gitRevision"]?.ToString(), Is.EqualTo(expectedSha),
                    packageId + ": PackageInfo.git.revision must keep the pinned request");
            }

            var registry = package["registry"] as JObject;
            Assert.That(registry, Is.Not.Null);
            Assert.That(registry!["operationCount"]?.Value<int>(), Is.GreaterThanOrEqualTo(1),
                packageId + " contributed no registered Operation — " +
                "Registry provenance is untestable (extension rejected? see candidateFailures)");
            var commits = registry!["resolvedCommits"]!
                .Values<string?>()
                .Where(c => c is not null)
                .Distinct()
                .ToList();
            Assert.That(commits, Has.Count.EqualTo(1),
                packageId + ": every registered Operation of one package must share " +
                "the package's single resolved commit");
            Assert.That(commits[0], Is.EqualTo(lockHash),
                packageId + ": the Registry's resolved commit differs from the lock's");
        }

        // ---------- local:T-W3.8-01:git-upm-resolution ----------

        // "Both independent Git remotes resolve; lock records their full
        // pinned commits; Registry shows the same commits."
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void PinnedGitUrls_RecordMatchingCommitsInLockAndRegistry()
        {
            var verdict = RunCase(
                "pinned",
                WorkflowGitRepo + "?path=" + WorkflowGitPath + "#" + WorkflowPinnedSha,
                SampleGitRepo + "#" + SamplePinnedSha);

            AssertVerdictHealth(verdict);
            AssertPackage(verdict, WorkflowPackageId, WorkflowGitRepo, WorkflowPinnedSha);
            AssertPackage(verdict, SamplePackageId, SampleGitRepo, SamplePinnedSha);
        }

        // "Unpinned or tag references still record resolved commits."
        // HEAD is the unpinned form the remotes always have; the SHA it
        // resolves to at run time is asserted for 40-hex shape and for
        // three-way consistency, never equality with the pinned value.
        [Test]
        [Timeout(45 * 60 * 1000)]
        public void UnpinnedGitUrls_StillRecordResolvedCommits()
        {
            var verdict = RunCase(
                "unpinned",
                WorkflowGitRepo + "?path=" + WorkflowGitPath,
                SampleGitRepo);

            AssertVerdictHealth(verdict);
            AssertPackage(verdict, WorkflowPackageId, WorkflowGitRepo, null);
            AssertPackage(verdict, SamplePackageId, SampleGitRepo, null);
        }
    }
}
