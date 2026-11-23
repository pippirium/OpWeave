#nullable enable

// T-W3.8-01 [Edit] child-side -executeMethod entry — the collection half
// of local:T-W3.8-01:git-upm-resolution (SPEC §T.1 G-06, §X.3, §X.4).
// It runs inside a DISPOSABLE child project the parent test builds under
// TestProject/Library/OpWeaveG06Probe/: the child's own
// Packages/manifest.json resolves com.opweave.workflow from
// https://github.com/pippirium/OpWeave-dev.git?path=/Packages/com.opweave.workflow
// and com.opweave.sample.operations from
// https://github.com/pippirium/OpWeave-Sample-Operations.git — two real,
// independent Git remotes. The shared TestProject manifest is never
// touched: the G-06 check needs a project whose manifest carries the Git
// references, not the checkout's file: reference.
//
// The entry reads the child's Packages/packages-lock.json, runs the real
// SPEC §C3 production registry build (RegistryBuildHost.RebuildProduction
// — the same call the startup hook list makes) and writes
// <job>/verdict.json with three independent views of each package: the
// lock entry's resolved hash, UPM's PackageInfo (git.hash / git.revision)
// and the published RegistrySnapshot's per-Operation PackageId /
// ResolvedCommit — the SPEC §C3 / §X.3 provenance slots. The parent test
// asserts all three agree for both remotes.
//
// This file is COPIED verbatim into the child's Assets/ under a probe
// asmdef named OpWeave.G06Probe. It therefore stays self-contained: no
// NUnit, no EditorProcessHarness and no other test-assembly helper may be
// referenced here; it compiles alone inside the child domain.
//
// Why OpWeave.G06Probe and not an InternalsVisibleTo name: every name the
// production InternalsVisibleTo attributes grant is also the name of an
// asmdef com.opweave.workflow itself ships —
// Tests/Editor/OpWeave.UnityAdapter.Tests.asmdef,
// Tests/Core/OpWeave.Core.Tests.asmdef and the production asmdefs — and a
// probe asmdef carrying any of them duplicates the Git-installed
// package's own assembly ("Assembly with name 'X' already exists"), a
// hard compile error. No production InternalsVisibleTo may be added for
// this check, so the two internals the verdict needs —
// RegistryBuildHost.RebuildProduction() and the retained build result —
// are reached through reflection, the only channel a foreign assembly
// legally has. The source additionally compiles against BOTH the
// checked-out package (in the parent test assembly) and the PINNED Git
// package's older API surface in the child, so members that post-date the
// pin (RegistrySnapshot.Exclusions) are read through the same seam.

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.G06Probe
{
    /// <summary>The batchmode <c>-executeMethod</c> entry of the G-06
    /// child Editor process spawned by the
    /// <c>OpWeave.UnityAdapter.Tests.GitUpmResolutionTests</c> suite.</summary>
    public static class GitUpmResolutionEntry
    {
        // The OWP_EPH_* environment contract EditorProcessHarness writes
        // for every spawned child; this entry cannot reference the harness
        // (it is not compiled in the child), so the literal repeats here on
        // purpose.
        private const string JobDirEnvVar = "OWP_EPH_JOB";

        private const string WorkflowPackageId = "com.opweave.workflow";
        private const string SamplePackageId = "com.opweave.sample.operations";

        /// <summary>The <c>-executeMethod</c> body: collect the G-06
        /// verdict and exit. A failure lands in fatal.txt with a non-zero
        /// exit code — a child that cannot produce its evidence is a failed
        /// check, never a quiet pass.</summary>
        public static void Run()
        {
            // SPEC §G1 item 3 (T-W4.6-06): an asset import worker inherits
            // the launched child's whole environment but is never the
            // process the parent spawned — it writes no verdict.
            if (AssetDatabase.IsAssetImportWorkerProcess())
            {
                return;
            }
            var jobDir = Environment.GetEnvironmentVariable(JobDirEnvVar) ?? "";
            try
            {
                AtomicWrite(Path.Combine(jobDir, "verdict.json"), Collect().ToString());
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                // The -logFile keeps every fatal: a child that dies before
                // verdict.json explains itself there even when the parent
                // never reads fatal.txt.
                Debug.LogError("G-06 child fatal: " + exception);
                try
                {
                    AtomicWrite(Path.Combine(jobDir, "fatal.txt"), exception.ToString());
                }
                catch (Exception)
                {
                    // Best effort — a full disk must not mask the real failure.
                }
                EditorApplication.Exit(3);
            }
        }

        /// <summary>The compile-time absolute path of this source file in
        /// the checkout — the parent test copies it into the child's probe
        /// assembly so one file is the single source for both
        /// compilations. CallerFilePath bakes the compiling path in; in the
        /// child the member exists but stays unused.</summary>
        internal static string SourcePath() => ThisFile();

        // CallerFilePath is bound at the CALL SITE: a parameter on
        // SourcePath() itself would be filled with the caller's path —
        // GitUpmResolutionTests.cs — and the test fixture would be copied
        // into the child in place of this file. The attribute must sit on
        // a helper invoked from inside THIS source file.
        private static string ThisFile([CallerFilePath] string path = "") => path;

        private static JObject Collect()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var lockPath = Path.Combine(projectRoot, "Packages", "packages-lock.json");
            var lockDependencies = File.Exists(lockPath)
                ? JObject.Parse(File.ReadAllText(lockPath))["dependencies"] as JObject
                : null;

            // The real §C3 build, driven exactly as the production startup
            // hook drives it: stages 1–8 over the live Editor seams, then
            // the atomic publication. The retained result also carries the
            // compile-consistency flag the verdict records for diagnosis.
            RebuildRegistry();
            var build = StaticMember(s_buildHost, "Latest");
            var snapshot = Member(build, "Snapshot") as RegistrySnapshot;
            var stale = Member(build, "Stale") as bool? ?? false;
            var compilationConsistent =
                Member(build, "CompilationConsistent") as bool? ?? false;

            var packages = new JObject
            {
                [WorkflowPackageId] = DescribePackage(WorkflowPackageId, lockDependencies, snapshot),
                [SamplePackageId] = DescribePackage(SamplePackageId, lockDependencies, snapshot),
            };

            var candidateFailures = new JArray();
            var exclusions = new JArray();
            if (snapshot is not null)
            {
                foreach (var failure in snapshot.CandidateFailures)
                {
                    candidateFailures.Add(new JObject
                    {
                        ["kind"] = failure.Kind.ToString(),
                        ["candidateId"] = failure.CandidateId,
                        ["assemblyName"] = failure.AssemblyName,
                        ["codes"] = new JArray(failure.Diagnostics.Select(d => d.Code)),
                    });
                }
                // RegistrySnapshot.Exclusions (the §C3 rule-5 policy
                // exclusion list) was added after the pinned
                // com.opweave.workflow commit — the member is read through
                // the same reflection seam so this one source compiles
                // against both package generations; absent is reported as
                // an empty list, never an error.
                var excluded =
                    Member(snapshot, "Exclusions") as IEnumerable ??
                    Array.Empty<object>();
                foreach (var exclusion in excluded)
                {
                    exclusions.Add(new JObject
                    {
                        ["kind"] = Member(exclusion, "Kind")?.ToString(),
                        ["candidateId"] = Member(exclusion, "CandidateId") as string,
                        ["packageId"] = Member(exclusion, "PackageId") as string,
                        ["assemblyName"] = Member(exclusion, "AssemblyName") as string,
                        ["reason"] = Member(exclusion, "Reason") as string,
                    });
                }
            }

            return new JObject
            {
                ["unityVersion"] = Application.unityVersion,
                ["snapshotPublished"] = snapshot is not null,
                ["stale"] = stale,
                ["compilationConsistent"] = compilationConsistent,
                ["packages"] = packages,
                ["candidateFailures"] = candidateFailures,
                ["exclusions"] = exclusions,
            };
        }

        // ── the §C3 build seam, reached without InternalsVisibleTo ─────
        //
        // RegistryBuildHost is internal to OpWeave.UnityAdapter and every
        // InternalsVisibleTo name that would reach it is also the name of
        // an asmdef the Git-installed package itself ships — a probe
        // asmdef using any of them collides with the package's own copy at
        // asmdef scan. The probe therefore invokes the SAME internal
        // parameterless RebuildProduction() entry the startup hook list
        // calls, through reflection, and reads the retained
        // RegistryBuildResult's Snapshot / Stale / CompilationConsistent
        // properties — the internal record type is only ever touched as
        // object; the RegistrySnapshot it hands back is public API.
        private static readonly Type s_buildHost =
            typeof(UnityStepContext).Assembly.GetType(
                "OpWeave.UnityAdapter.RegistryBuildHost", throwOnError: true)!;

        private static void RebuildRegistry()
        {
            var method = s_buildHost.GetMethod(
                "RebuildProduction",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (method is null)
            {
                throw new InvalidOperationException(
                    "OpWeave.UnityAdapter.RegistryBuildHost.RebuildProduction() is not present");
            }
            method.Invoke(null, null);
        }

        private static object? StaticMember(Type type, string name) =>
            type.GetProperty(
                    name,
                    BindingFlags.Static |
                    BindingFlags.NonPublic |
                    BindingFlags.Public)
                ?.GetValue(null);

        private static object? Member(object? instance, string name) =>
            instance?.GetType()
                .GetProperty(
                    name,
                    BindingFlags.Instance |
                    BindingFlags.NonPublic |
                    BindingFlags.Public)
                ?.GetValue(instance);

        private static JObject DescribePackage(
            string packageId, JObject? lockDependencies, RegistrySnapshot? snapshot)
        {
            var lockEntry = lockDependencies?[packageId] as JObject;

            UnityEditor.PackageManager.PackageInfo? info = null;
            try
            {
                info = UnityEditor.PackageManager.PackageInfo.FindForPackageName(packageId);
            }
            catch (Exception)
            {
                // A package UPM cannot name is recorded as absent — the
                // parent's assertions decide whether that is a failure.
            }

            var operations = snapshot?.Operations
                .Where(o => string.Equals(o.PackageId, packageId, StringComparison.Ordinal))
                .ToArray()
                ?? Array.Empty<RegisteredOperation>();

            return new JObject
            {
                ["lock"] = lockEntry is null
                    ? null
                    : new JObject
                    {
                        ["version"] = lockEntry["version"]?.ToString(),
                        ["source"] = lockEntry["source"]?.ToString(),
                        ["url"] = lockEntry["url"]?.ToString(),
                        ["hash"] = lockEntry["hash"]?.ToString(),
                    },
                ["packageInfo"] = info is null
                    ? null
                    : new JObject
                    {
                        ["name"] = info.name,
                        ["packageId"] = info.packageId,
                        ["version"] = info.version,
                        ["source"] = info.source.ToString(),
                        ["gitHash"] = info.git?.hash,
                        ["gitRevision"] = info.git?.revision,
                    },
                ["registry"] = new JObject
                {
                    ["operationCount"] = operations.Length,
                    ["resolvedCommits"] =
                        JArray.FromObject(operations.Select(o => o.ResolvedCommit).Distinct()),
                    ["operations"] = new JArray(operations.Select(o => new JObject
                    {
                        ["id"] = o.Descriptor.Id,
                        ["version"] = o.Descriptor.Version,
                        ["assemblyName"] = o.AssemblyName,
                        ["resolvedCommit"] = o.ResolvedCommit,
                    })),
                },
            };
        }

        // Write-then-rename, the closest .NET Standard 2.1 comes to atomic:
        // a polling parent never sees a half-written verdict.
        private static void AtomicWrite(string path, string content)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temporary, path);
        }
    }
}
