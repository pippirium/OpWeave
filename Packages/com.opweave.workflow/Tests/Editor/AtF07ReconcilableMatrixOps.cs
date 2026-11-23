#nullable enable

// T-W4.6-04 [Edit] shared machinery for local:T-W4.6-04:atf07-reconcilable-20x6
// — the twenty standard Reconcilable Operations of SPEC §I driven through the
// six §F5 cases at the real Runner/recovery boundary (AT-F07). This file owns
// the per-Operation spec table (world prep, plan inputs, the case-specific
// disturbance and the world assertions each §F5 verdict prescribes) plus the
// [op -> hold] park, journal surgery, recovery pass and Git fixture helpers
// both the in-domain suite (cases 1-5, AtF07ReconcilableMatrixTests) and the
// real-Editor-restart child scenarios (case 6, InterruptionScenarioEntry's
// arrange-matrix / verify-matrix) share.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Recovery;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The §F5 per-Operation world description the AT-F07 matrix drives: what
    /// the arrange phase builds, how each interruption case perturbs it, and
    /// what the recovered world must look like. Cases share the park /
    /// truncate / recover skeleton; only the world differs — exactly the
    /// injection the §F5 six-case table prescribes.
    /// </summary>
    internal enum MatrixVerdict
    {
        /// NotApplied → the resumed step re-executes once, parks at the hold.
        Reexec,

        /// Completed → the recorded evidence is adopted (reconciled:true).
        Adopted,

        /// Unknown → NeedsReview, no automatic success or re-execution.
        Review,
    }

    internal sealed class MatrixOp
    {
        internal string Key = "";
        internal string OpId = "";
        internal char Seed;
        internal Action Prep = () => { };
        internal Func<Dictionary<string, InputSource>> Inputs = () =>
            new Dictionary<string, InputSource>();
        internal IReadOnlyDictionary<string, string>? Outputs;
        internal Action Applied = () => { };
        internal MatrixVerdict C1 = MatrixVerdict.Review;
        internal Action? Unapply;
        internal Action? AfterC1;
        internal MatrixVerdict C2 = MatrixVerdict.Adopted;
        internal Action? AfterC2;
        internal Action? C3;
        internal Action? AfterC3;
        internal bool C4Refused;
        internal Action? C4Prep;
        internal Action? C4Unapply;
        internal MatrixVerdict C4 = MatrixVerdict.Review;
        internal Action? AfterC4;
        internal Action? C5;
        internal Action? AfterC5;
        // The package Operations' pre-state view override for a case whose
        // recorded snapshot must differ from the default pre-state.
        internal Func<PackageInstallView>? C4AddView;
        // A hook between accept and the parking tick — the
        // prefab.save_contents ownership binding runs there.
        internal Action<string>? Bind;
    }

    internal static class AtF07Matrix
    {
        internal static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        internal static string ProbeDir =>
            Path.Combine(ProjectRoot, "Library", "OpWeaveAtf07Matrix");

        internal static string StoreRootFor(string key) =>
            Path.Combine(ProbeDir, "run-" + key);

        internal const string HoldOp = "test.atf07matrix.hold";
        internal const string WorkflowId = "example.atf07.matrix";
        internal const string Folder = "Assets/OpWeaveAtf07M";

        internal const string PkgName = "com.opweave.fixture.atf07git";
        internal const string PkgTag = "vatf07";
        internal static readonly string[] OpKeys =
        {
            "scene.create", "go.create", "go.delete",
            "component.add", "component.remove",
            "prefab.create_from", "prefab.instantiate", "prefab.unpack",
            "prefab.create_variant", "prefab.open_contents",
            "prefab.save_contents",
            "asset.create_material", "asset.create_so",
            "asset.move", "asset.copy", "asset.delete",
            "script.template", "script.create",
            "package.add", "package.remove",
        };

        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        internal static string Disk(string assetPath) =>
            Path.Combine(
                ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));

        internal static string P(string key, string file) => Folder + "/" + key + "/" + file;

        // ── Registration ────────────────────────────────────────────────

        // Every Operation of the matrix at its fingerprint seed, the package
        // Operations over the deterministic three-channel view seam, and the
        // hold Operation that parks each Run mid-flight — one registration
        // set shared by the in-domain suite and the restart child.
        internal static void RegisterAll(
            RunnerHostFixture fixture,
            Func<PackageInstallView> addView,
            Func<PackageInstallView> removeView)
        {
            Reg(fixture, new CreateSceneOperation(), 'a');
            Reg(fixture, new GameObjectCreateOperation(), 'c');
            Reg(fixture, new GameObjectDeleteOperation(), 'e');
            Reg(fixture, new ComponentAddOperation(), '0');
            Reg(fixture, new ComponentRemoveOperation(), '2');
            Reg(fixture, new PrefabCreateFromGameObjectOperation(), 'b');
            Reg(fixture, new PrefabInstantiateOperation(), 'd');
            Reg(fixture, new PrefabUnpackOperation(), 'f');
            Reg(fixture, new PrefabCreateVariantOperation(), 'h');
            Reg(fixture, new PrefabOpenContentsOperation(), 'j');
            Reg(fixture, new PrefabSaveContentsOperation(), 'k');
            Reg(fixture, new CreateMaterialOperation(), 'm');
            Reg(fixture, new CreateScriptableObjectOperation(), 'n');
            // The allowed-path seams the default ctors cannot reach in a
            // fixture: the default binding reads OpWeaveRunnerHost.Current
            // — null without Install — and fails closed, while the real
            // production binding resolves the executing Run's accepted
            // settings snapshot (SPEC §H4.1). script.create's codePaths
            // scope the write to the matrix folder only.
            Reg(fixture, new MoveAssetOperation(
                    () => fixture.Host.ActiveRunAllowedPaths), 'p');
            Reg(fixture, new CopyAssetOperation(), 'q');
            Reg(fixture, new DeleteAssetOperation(
                    () => fixture.Host.ActiveRunAllowedPaths), 'r');
            Reg(fixture, new CreateScriptFromTemplateOperation(), 's');
            Reg(fixture, new CreateScriptOperation(
                    () => new[] { Folder + "/" }), 't');
            Reg(fixture, new UnityPackageAddOperation(
                    static () => Result<OpWeaveSettings>.Success(FixtureSettings()),
                    static id => PackageResolvedCondition.InitialToken(id, true),
                    new DoneCondition(),
                    addView),
                'u');
            Reg(fixture, new UnityPackageRemoveOperation(
                    static name => PackageResolvedCondition.InitialToken(name, false),
                    new DoneCondition(),
                    removeView),
                'v');
            Reg(fixture,
                new RunnerHostFixture.ControlledWaitableOperation(HoldOp), '4');
            // The type_exists requirements of component.add and
            // asset.create_so read the injected static-type probe — the
            // same Probe.Know seeding the AT-02 recovery fixtures use.
            fixture.Probe.Know("UnityEngine.BoxCollider");
            fixture.Probe.Know(typeof(ProbeFieldsAsset).FullName!);
        }

        // The 64-hex fingerprint pair a RegisteredOperation and its Plan
        // step must agree on byte-exactly (SPEC §D7 / PlanJsonCodec's
        // steps.descriptorHash and steps.implementationHash rules): the
        // seeded-character scheme of RunnerHostFixture.Step cannot sign the
        // non-hex seeds this table uses, so the matrix derives its pair
        // deterministically instead — the same value in the in-domain
        // fixture and in the arrange/verify children across the real
        // process boundary of case 6.
        internal static string Fingerprint(char seed, int slot) =>
            CanonicalJson.Sha256(new JValue("atf07-matrix:" + slot + ":" + seed));

        private static void Reg(RunnerHostFixture fixture, IOperation op, char seed) =>
            fixture.Operations.Add(new RegisteredOperation(
                op.Descriptor,
                OperationOrigin.Standard,
                "Example.Operations",
                null,
                Fingerprint(seed, 0),
                Fingerprint(seed, 1))
            {
                Implementation = op,
            });

        internal static OpWeaveSettings FixtureSettings()
        {
            var parsed = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"allowedPackageSources\":[" +
                "\"registry:com.unity.*\"," +
                "\"git:" + GitOrigin + "\"]}");
            Assert.IsTrue(parsed.Ok, "fixture settings must parse");
            return parsed.Value!;
        }

        private sealed class DoneCondition : IWaitCondition
        {
            public WaitStatus Check(JsonValue token) => WaitStatus.Done();
        }

        // ── The local Git fixture repository ────────────────────────────

        // A real local repository the test creates, commits to and tags —
        // the Git URL form per T-W2.12-05, no external remote. The dep URL
        // is the admissible file:// origin; the tag the matrix exercises is
        // created (and for the changed-commit case, moved) locally.
        internal static string FixtureRoot => Path.Combine(ProbeDir, "gitrepo");
        internal static string BareRepo => Path.Combine(FixtureRoot, "bare.git");

        // The admissible SG-19 Git URL form per T-W2.12-05 — the same
        // fixture.opweave.invalid shape the Git reload tests use. The
        // deterministic package-state seam never fetches the dep, so no
        // url.<local>.insteadOf rewrite is armed; the local bare
        // repository the test builds supplies every commit SHA the
        // evidence channels report, with tags created and moved locally
        // and no external remote.
        internal const string GitOrigin =
            "https://fixture.opweave.invalid/atf07/repo.git";
        internal static string GitDep(string revision) =>
            GitOrigin + "?path=/pkgs/pkg#" + revision;

        internal sealed class GitResult
        {
            internal int Exit;
            internal string StdOut = "";
            internal string StdErr = "";
            internal string FirstLine => (StdOut + StdErr)
                .Split('\n').FirstOrDefault(l => l.Length > 0)?.Trim() ?? "";
        }

        internal static GitResult Git(string args, string? workDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workDir ?? ProjectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var result = new GitResult();
            using (var p = Process.Start(psi)!)
            {
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(60000))
                {
                    try { p.Kill(); } catch { /* best effort */ }
                    Assert.Fail("git timed out: " + args);
                }
                result.Exit = p.ExitCode;
                result.StdOut = so.Result;
                result.StdErr = se.Result;
            }
            return result;
        }

        internal static void AssertGit(GitResult r, string what) =>
            Assert.That(r.Exit, Is.EqualTo(0),
                $"git {what} failed (exit {r.Exit}): {r.FirstLine}");

        // Builds the fixture repository: a package directory under pkgs/pkg,
        // two commits, and the vatf07 tag pinned to the first — returns the
        // tagged commit and HEAD so the matrix can pin and later move the
        // tag. Idempotent across repeated calls on an existing fixture.
        internal static (string taggedSha, string headSha) EnsureGitFixture()
        {
            var src = Path.Combine(FixtureRoot, "src");
            if (!Directory.Exists(BareRepo))
            {
                Directory.CreateDirectory(Path.Combine(src, "pkgs", "pkg"));
                File.WriteAllText(
                    Path.Combine(src, "pkgs", "pkg", "package.json"),
                    "{\n  \"name\": \"" + PkgName + "\",\n" +
                    "  \"version\": \"1.0.0\",\n  \"unity\": \"6000.0\"\n}\n");
                AssertGit(Git("init", src), "init");
                AssertGit(Git("config user.email atf07@fixture", src), "user.email");
                AssertGit(Git("config user.name atf07", src), "user.name");
                AssertGit(Git("add .", src), "add");
                AssertGit(Git("commit -m c1", src), "commit c1");
                File.WriteAllText(
                    Path.Combine(src, "pkgs", "pkg", "second.txt"), "two\n");
                AssertGit(Git("add .", src), "add 2");
                AssertGit(Git("commit -m c2", src), "commit c2");
                var tagged = Git("rev-parse HEAD~1", src).StdOut.Trim();
                AssertGit(Git("tag -f " + PkgTag + " " + tagged, src), "tag");
                AssertGit(
                    Git("clone --bare " + src + " " + BareRepo, FixtureRoot),
                    "bare clone");
            }
            // MoveTagToHead permanently moves the shared tag — re-pin it to
            // the first commit on every call, whether the bare repository
            // was just built or already existed, so the dep's tagged
            // revision is deterministic per case and per run.
            var first = Git("rev-parse HEAD~1", BareRepo);
            AssertGit(first, "rev-parse HEAD~1");
            AssertGit(
                Git("tag -f " + PkgTag + " " + first.StdOut.Trim(), BareRepo),
                "re-pin tag");
            var t = Git("rev-parse --verify refs/tags/" + PkgTag, BareRepo);
            AssertGit(t, "rev-parse tag");
            var h = Git("rev-parse HEAD", BareRepo);
            AssertGit(h, "rev-parse HEAD");
            return (t.StdOut.Trim(), h.StdOut.Trim());
        }

        // Moves the local tag to a different commit and returns the new sha —
        // the "artifact changed by another operation" package injection.
        internal static string MoveTagToHead()
        {
            var head = Git("rev-parse HEAD", BareRepo);
            AssertGit(head, "rev-parse HEAD");
            var sha = head.StdOut.Trim();
            AssertGit(Git("tag -f " + PkgTag + " " + sha, BareRepo), "tag -f");
            return sha;
        }

        // ── PackageInstallView builders (the three-channel seam) ────────

        internal static PackageInstallView AbsentView() =>
            new PackageInstallView(
                new JObject(), new JObject(), Array.Empty<ResolvedPackageView>());

        internal static PackageInstallView GitInstalledView(
            string dep, string commit, string requested) =>
            new PackageInstallView(
                new JObject { [PkgName] = dep },
                new JObject
                {
                    [PkgName] = new JObject
                    {
                        ["version"] = dep,
                        ["source"] = "git",
                        ["hash"] = commit,
                    },
                },
                new[]
                {
                    new ResolvedPackageView(
                        PkgName, "1.0.0", "git", dep, commit, requested),
                });

        // Manifest entry landed, lockfile and registry silent — the partial
        // side effect of case 3.
        internal static PackageInstallView PartialAddView(string dep) =>
            new PackageInstallView(
                new JObject { [PkgName] = dep },
                new JObject(), Array.Empty<ResolvedPackageView>());

        // Manifest and lockfile cleared, the registered list still carries
        // the package — the partial remove of case 3.
        internal static PackageInstallView PartialRemoveView(
            string dep, string commit, string requested) =>
            new PackageInstallView(
                new JObject(), new JObject(),
                new[]
                {
                    new ResolvedPackageView(
                        PkgName, "1.0.0", "git", dep, commit, requested),
                });

        // A same-name package whose provenance is not the recorded Git
        // install — a registry artifact standing where the remove's
        // evidence row points (case 4 for package.remove).
        internal static PackageInstallView RegistryInstalledView() =>
            new PackageInstallView(
                new JObject { [PkgName] = "9.9.9" },
                new JObject
                {
                    [PkgName] = new JObject
                    {
                        ["version"] = "9.9.9",
                        ["source"] = "registry",
                    },
                },
                new[]
                {
                    new ResolvedPackageView(
                        PkgName, "9.9.9", "registry", "9.9.9", null, null),
                });

        // ── Park / truncate / recover ───────────────────────────────────

        internal static PlanStep Step(
            int index, string nodeId, string operationId, char hashSeed,
            IReadOnlyDictionary<string, InputSource>? inputs = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            int next = -1, Idempotency idempotency = Idempotency.Unsafe) =>
            new PlanStep(
                index,
                nodeId,
                operationId,
                1,
                Fingerprint(hashSeed, 0),
                Fingerprint(hashSeed, 1),
                inputs ?? new Dictionary<string, InputSource>(),
                outputTypes ?? new Dictionary<string, string>(),
                new Dictionary<string, int> { ["next"] = next },
                Array.Empty<string>(),
                30,
                new RetryPolicy(),
                idempotency);

        internal static WorkflowDocument Doc(
            IReadOnlyList<(string id, string operation)> nodes,
            params (string from, string to)[] edges) =>
            RunnerHostFixture.Doc(
                WorkflowId,
                nodes.Select(n => new NodeData
                    { Id = n.id, Operation = n.operation, Version = 1 }).ToList(),
                edges.Select(e => new ControlEdge(e.from, e.to)).ToList());

        internal static string Accept(
            RunnerHostFixture fixture, CompiledPlan plan, WorkflowDocument doc)
        {
            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, new AcceptOptions(null, false));
            Assert.IsTrue(accepted.Ok,
                "accept failed: " + Describe(accepted.Diagnostics));
            return accepted.Value!.RunId;
        }

        internal static void Tick(RunnerHostFixture fixture)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            var tick = fixture.Host.Tick();
            Assert.IsTrue(tick.Ok, "tick failed: " + Describe(tick.Diagnostics));
        }

        internal static RunRecoveryReport Recover(RunnerHostFixture fixture)
        {
            var recovered = fixture.Host.RecoverRuns();
            Assert.IsTrue(recovered.Ok,
                "recovery failed: " + Describe(recovered.Diagnostics));
            Assert.IsNotNull(recovered.Value);
            return recovered.Value!;
        }

        // Ticks the recovered Run to its decided state: a NeedsReview Run
        // stops immediately (no resume was journaled), a resumed Run
        // settles when the hold's step_waiting{step:1} is the journal's
        // last durable record — the re-executed or adopted step 0 has then
        // run its full course and the Run is parked mid-flight again.
        internal static void Settle(RunnerHostFixture fixture, string runId)
        {
            var journal = RunnerHostFixture.JournalPath(fixture.Root, runId);
            for (var i = 0; i < 40; i++)
            {
                var state = fixture.Host.Coordinator.StateOf(runId);
                if (state == RunState.NeedsReview)
                {
                    return;
                }
                var lines = File.Exists(journal)
                    ? File.ReadAllLines(journal)
                    : Array.Empty<string>();
                if (lines.Length != 0 &&
                    lines[lines.Length - 1].Contains("\"type\":\"step_waiting\"") &&
                    lines[lines.Length - 1].Contains("\"step\":1"))
                {
                    var flushed = fixture.Host.FlushJournal();
                    Assert.IsTrue(flushed.Ok,
                        "journal flush failed: " + Describe(flushed.Diagnostics));
                    return;
                }
                Assert.AreEqual(
                    RunState.Running, state,
                    "the resumed Run left Running without parking or " +
                    "NeedsReview; journal: " + string.Join(" | ", lines));
                Tick(fixture);
            }
            Assert.Fail("the resumed Run never settled; journal: " +
                string.Join(" | ", Lines(fixture.Root, runId)));
        }

        // Accepts [step -> hold] and ticks: step 0 executes and the Run
        // parks mid-flight — at the hold's step_waiting{step:1}, or at the
        // Operation's own step_waiting{step:0} for waitable package steps.
        internal static string Park(RunnerHostFixture fixture, MatrixOp op)
        {
            var steps = new[]
            {
                Step(0, "s", op.OpId, op.Seed, next: 1,
                    idempotency: Idempotency.Reconcilable,
                    inputs: op.Inputs(), outputTypes: op.Outputs),
                Step(1, "h", HoldOp, '4', next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = Doc(new[] { ("s", op.OpId), ("h", HoldOp) }, ("s", "h"));
            var runId = Accept(fixture, plan, doc);
            op.Bind?.Invoke(runId);
            var journal = RunnerHostFixture.JournalPath(fixture.Root, runId);
            for (var i = 0; i < 40; i++)
            {
                Tick(fixture);
                var lines = File.Exists(journal)
                    ? File.ReadAllLines(journal)
                    : Array.Empty<string>();
                if (lines.Length != 0 &&
                    lines[lines.Length - 1].Contains("\"type\":\"step_waiting\""))
                {
                    var flushed = fixture.Host.FlushJournal();
                    Assert.IsTrue(flushed.Ok,
                        "journal flush failed: " + Describe(flushed.Diagnostics));
                    Assert.AreEqual(
                        RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                        op.OpId + " did not park mid-flight; journal: " +
                        string.Join(" | ", RunnerHostFixture.JournalLines(
                            fixture.Root, runId)));
                    return runId;
                }
                Assert.AreEqual(
                    RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                    op.OpId + " left Running without parking; journal: " +
                    string.Join(" | ", RunnerHostFixture.JournalLines(
                        fixture.Root, runId)));
            }
            Assert.Fail(op.OpId + " never reached step_waiting; journal: " +
                string.Join(" | ", RunnerHostFixture.JournalLines(
                    fixture.Root, runId)));
            return runId;
        }

        internal static string[] Lines(string root, string runId) =>
            RunnerHostFixture.JournalLines(root, runId);

        internal static void TruncateStarted(string root, string runId)
        {
            var lines = Lines(root, runId);
            var cut = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("\"type\":\"step_started\"") &&
                    lines[i].Contains("\"step\":0"))
                {
                    cut = i;
                    break;
                }
            }
            Assert.GreaterOrEqual(cut, 0,
                "the journal never recorded step_started{0}: " +
                string.Join(" | ", lines));
            File.WriteAllText(
                RunnerHostFixture.JournalPath(root, runId),
                string.Join("\n", lines.Take(cut + 1)) + "\n", Utf8);
        }

        internal static void AssertNeedsReview(
            string root, RunnerHostFixture fixture, string runId, string because)
        {
            Assert.AreEqual(
                RunState.NeedsReview, fixture.Host.Coordinator.StateOf(runId),
                because + " must end NeedsReview; journal: " +
                string.Join(" | ", Lines(root, runId)));
            Assert.IsFalse(
                RunnerHostFixture.JournalHas(root, runId, "\"type\":\"run_resumed\""),
                because + ": a review-needed resume never journals run_resumed");
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    root, runId, "\"type\":\"step_needs_review\"", "\"step\":0"),
                because + ": the Unknown verdict is journaled step_needs_review");
        }

        // The adopted (Completed) resume: the recorded evidence row held,
        // so the recovery pass journals the reconciled success and the Run
        // continues to the parked hold — never a second Execute.
        internal static void AssertAdopted(
            string root, RunnerHostFixture fixture, string runId, string because)
        {
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    root, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"reconciled\":true"),
                because + ": the held evidence row is adopted step_succeeded " +
                "reconciled:true; journal: " + string.Join(" | ", Lines(root, runId)));
            Assert.AreEqual(
                RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                because + ": the adopted Run must continue to the parked hold");
        }

        // The re-executed (NotApplied) resume: run_resumed, the step runs
        // again and reaches step_succeeded{step:0} without a reconciled
        // adoption, and the Run parks at the hold again.
        internal static void AssertReexecuted(
            string root, RunnerHostFixture fixture, string runId, string because)
        {
            Assert.IsTrue(
                RunnerHostFixture.JournalHas(root, runId, "\"type\":\"run_resumed\""),
                because + ": the NotApplied verdict resumes the Run; journal: " +
                string.Join(" | ", Lines(root, runId)));
            Assert.IsTrue(
                RunnerHostFixture.JournalHasAll(
                    root, runId, "\"type\":\"step_succeeded\"", "\"step\":0"),
                because + ": the re-executed step reaches step_succeeded{0}");
            Assert.AreEqual(
                RunState.Running, fixture.Host.Coordinator.StateOf(runId),
                because + ": the re-executed Run must re-park at the hold");
        }

        internal static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
            {
                return "<no diagnostics>";
            }
            return string.Join("; ",
                diagnostics.Select(d => d.Code + " " + d.Message));
        }

        // ── World helpers ───────────────────────────────────────────────

        internal static void EnsureFolder(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                var parent = Path.GetDirectoryName(folder)!.Replace('\\', '/');
                EnsureFolder(parent);
                AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            }
        }

        internal static void WriteAssetFile(string assetPath, string content)
        {
            EnsureFolder(Path.GetDirectoryName(assetPath)!.Replace('\\', '/'));
            File.WriteAllText(Disk(assetPath), content);
            AssetDatabase.ImportAsset(assetPath);
        }

        internal static string CreateTextAsset(string assetPath, string content)
        {
            WriteAssetFile(assetPath, content);
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            Assert.That(guid, Is.Not.Empty, "no guid for " + assetPath);
            return guid;
        }

        internal static string CreatePrefab(string assetPath, string objectName)
        {
            var go = new GameObject(objectName);
            try
            {
                EnsureFolder(Path.GetDirectoryName(assetPath)!.Replace('\\', '/'));
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, assetPath);
                Assert.IsNotNull(prefab, "SaveAsPrefabAsset failed for " + assetPath);
                return AssetDatabase.AssetPathToGUID(assetPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        internal static void ForeignFile(string assetPath, string content)
        {
            File.WriteAllText(Disk(assetPath), content);
            AssetDatabase.Refresh();
        }

        // A torn write: the payload file exists but only half of it ever
        // landed — the §F5 partial side effect a content-identity evidence
        // row can never adopt. (Dropping the .meta alone never perturbs
        // the evidence: the artifact hash strips its guid line, so a
        // re-imported guid-renewed file hashes identically, and the
        // recorded guid may keep resolving to the live path.)
        internal static void TornWrite(string assetPath)
        {
            var disk = Disk(assetPath);
            var full = File.ReadAllText(disk);
            File.WriteAllText(disk, full.Substring(0, full.Length / 2));
            AssetDatabase.Refresh();
        }

        internal static GameObject FindByName(string name) =>
            UnityEngine.Object.FindObjectsByType<Transform>(
                    FindObjectsInactive.Include)
                .Select(t => t.gameObject)
                .FirstOrDefault(g => g.name == name)!;

        internal static int CountByName(string name) =>
            UnityEngine.Object.FindObjectsByType<Transform>(
                    FindObjectsInactive.Include)
                .Count(t => t.gameObject.name == name);

        internal const string BaseScenePath = Folder + "/Base.unity";

        // A saved, empty base scene is the deterministic pre-state every
        // matrix case runs from: a batchmode Editor opens on an unsaved
        // untitled scene whose presence makes unity.scene.create's additive
        // mode fail outright (OW-UNITY-011) and whose empty path no
        // scene-keyed evidence row can re-prove. NewScene(Single) also
        // closes whatever the previous case left loaded, so no foreign
        // scene leaks into a snapshot.
        internal static Scene EnsureBaseScene()
        {
            EnsureFolder(Folder);
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.IsTrue(
                EditorSceneManager.SaveScene(scene, BaseScenePath),
                "the matrix base scene must save");
            return scene;
        }

        // ── The case-6 restart arrange: per-Operation world prep and the
        // [op -> hold] park the arrange-matrix child runs for every key.
        // The child calls this on its own store root; the dep is the local
        // Git fixture URL the parent built (tags created and moved locally,
        // no external remote).

        // The SPEC §F5 case-6 verdict per Operation: file-identity and
        // persisted package evidence survive a real process boundary and
        // are adopted; session-bound Unity handles can never be re-proven
        // in a new Editor session (SPEC §F5 common rule 3) and end
        // NeedsReview; open_contents' dead session is provably absent and
        // re-executes once.
        internal static MatrixVerdict RestartVerdict(string key) =>
            key switch
            {
                "prefab.open_contents" => MatrixVerdict.Reexec,
                "scene.create" or "go.create" or "go.delete" or
                "component.add" or "component.remove" or
                "prefab.instantiate" or "prefab.unpack" => MatrixVerdict.Review,
                _ => MatrixVerdict.Adopted,
            };

        // The per-key plan inputs of the restart arrange — identical to the
        // in-domain spec table (the child's session can build every Ref a
        // parked plan needs).
        private static Constant Cst(string json) =>
            new Constant(RunnerHostFixture.Json(json));

        internal static (PlanStep[] steps, Action<string>? bind) ArrangeSteps(
            string key, string dep)
        {
            var folder = Folder + "/" + key;
            switch (key)
            {
                case "scene.create":
                    EnsureFolder(folder);
                    return (new[]
                    {
                        Step(0, "s", CreateSceneOperation.OperationId, 'a', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["path"] = Cst("\"" + folder + "/Created.unity\""),
                                ["setup"] = Cst("\"empty\""),
                                ["mode"] = Cst("\"additive\""),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["scene"] = "unity.SceneRef" }),
                    }, null);
                case "go.create":
                    return (new[]
                    {
                        Step(0, "s", GameObjectCreateOperation.OperationId, 'c', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                                { ["name"] = Cst("\"Atf07MGoCreate\"") },
                            outputTypes: new Dictionary<string, string>
                                { ["gameObject"] = "unity.GameObjectRef" }),
                    }, null);
                case "go.delete":
                {
                    var target = new GameObject("Atf07MGoDelete");
                    return (new[]
                    {
                        Step(0, "s", GameObjectDeleteOperation.OperationId, 'e', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["target"] = new Constant(new RefResolver().ToRef(
                                    target, "unity.GameObjectRef")),
                            }),
                    }, null);
                }
                case "component.add":
                    new GameObject("Atf07MCompAdd");
                    return (new[]
                    {
                        Step(0, "s", ComponentAddOperation.OperationId, '0', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["target"] = Cst(
                                    "{\"kind\":\"gameObject\",\"path\":\"/Atf07MCompAdd\"}"),
                                ["componentType"] = Cst("\"UnityEngine.BoxCollider\""),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["component"] = "unity.ComponentRef<unity.Component>" }),
                    }, null);
                case "component.remove":
                {
                    var go = new GameObject("Atf07MCompRemove");
                    go.AddComponent<BoxCollider>();
                    var comp = go.AddComponent<BoxCollider>();
                    return (new[]
                    {
                        Step(0, "s", ComponentRemoveOperation.OperationId, '2', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["component"] = new Constant(new RefResolver().ToRef(
                                    comp, "unity.ComponentRef<unity.Component>")),
                            }),
                    }, null);
                }
                case "prefab.create_from":
                    EnsureFolder(folder);
                    new GameObject("Atf07MSource");
                    return (new[]
                    {
                        Step(0, "s",
                            PrefabCreateFromGameObjectOperation.OperationId, 'b', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["source"] = Cst(
                                    "{\"kind\":\"gameObject\",\"path\":\"/Atf07MSource\"}"),
                                ["path"] = Cst("\"" + folder + "/FromGo.prefab\""),
                                ["connect"] = Cst("false"),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["prefab"] = "unity.PrefabAssetRef" }),
                    }, null);
                case "prefab.instantiate":
                {
                    var p = folder + "/Atf07MBase.prefab";
                    var g = CreatePrefab(p, "Atf07MBase");
                    return (new[]
                    {
                        Step(0, "s", PrefabInstantiateOperation.OperationId, 'd', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["prefab"] = new Constant(
                                    PrefabContentsFixture.PrefabRef(g, p)),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["instance"] = "unity.PrefabInstanceRef" }),
                    }, null);
                }
                case "prefab.unpack":
                {
                    var p = folder + "/Atf07MUnpack.prefab";
                    CreatePrefab(p, "Atf07MUnpack");
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(
                        AssetDatabase.LoadAssetAtPath<GameObject>(p));
                    return (new[]
                    {
                        Step(0, "s", PrefabUnpackOperation.OperationId, 'f', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["instance"] = new Constant(new RefResolver().ToRef(
                                    inst, "unity.PrefabInstanceRef")),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["gameObject"] = "unity.GameObjectRef" }),
                    }, null);
                }
                case "prefab.create_variant":
                {
                    var b = folder + "/VariantBase.prefab";
                    var g = CreatePrefab(b, "Atf07MVBase");
                    return (new[]
                    {
                        Step(0, "s", PrefabCreateVariantOperation.OperationId, 'h', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["base"] = new Constant(
                                    PrefabContentsFixture.PrefabRef(g, b)),
                                ["path"] = Cst("\"" + folder + "/Variant.prefab\""),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["variant"] = "unity.PrefabAssetRef" }),
                    }, null);
                }
                case "prefab.open_contents":
                {
                    var p = folder + "/OpenMe.prefab";
                    var g = CreatePrefab(p, "Atf07MOpen");
                    return (new[]
                    {
                        Step(0, "s", "unity.prefab.open_contents", 'j', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["prefab"] = new Constant(
                                    PrefabContentsFixture.PrefabRef(g, p)),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["root"] = "unity.GameObjectRef" }),
                    }, null);
                }
                case "prefab.save_contents":
                {
                    var p = folder + "/SaveMe.prefab";
                    var g = CreatePrefab(p, "Atf07MSave");
                    var contents = PrefabUtility.LoadPrefabContents(p);
                    var edit = new GameObject("Atf07MEdit");
                    edit.transform.SetParent(contents.transform, false);
                    return (new[]
                    {
                        Step(0, "s", "unity.prefab.save_contents", 'k', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["root"] = new Constant(new RefResolver(
                                        PrefabContentsResources.FindEditingRoot)
                                    .ToRef(contents, "unity.GameObjectRef")),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["prefab"] = "unity.PrefabAssetRef" }),
                    },
                    runId => PrefabContentsOwnership.Bind(
                        RunResourceScope.Of(runId, 0), g, contents));
                }
                case "asset.create_material":
                    WriteAssetFile(folder + "/Probe.shader",
                        "Shader \"OpWeave/Atf07Probe\"\n{\n" +
                        "    Properties { _Color (\"Color\", Color) = (1,1,1,1) }\n" +
                        "    SubShader { Pass { } }\n}\n");
                    return (new[]
                    {
                        Step(0, "s", CreateMaterialOperation.OperationId, 'm', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["path"] = Cst("\"" + folder + "/Mat.mat\""),
                                ["shader"] = Cst("\"OpWeave/Atf07Probe\""),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["material"] = "unity.AssetRef<unity.Material>" }),
                    }, null);
                case "asset.create_so":
                    EnsureFolder(folder);
                    return (new[]
                    {
                        Step(0, "s", CreateScriptableObjectOperation.OperationId, 'n',
                            next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["path"] = Cst("\"" + folder + "/Obj.asset\""),
                                ["type"] = Cst("\"" +
                                    typeof(ProbeFieldsAsset).FullName! + "\""),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["asset"] = "unity.AssetRef<unity.ScriptableObject>" }),
                    }, null);
                case "asset.move":
                {
                    var src = folder + "/Src.txt";
                    var g = CreateTextAsset(src, "atf07 move\n");
                    return (new[]
                    {
                        Step(0, "s", MoveAssetOperation.OperationId, 'p', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["asset"] = Cst("{\"kind\":\"asset\",\"guid\":\"" + g +
                                    "\",\"path\":\"" + src + "\"}"),
                                ["destination"] = Cst("\"" + folder + "/Dst.txt\""),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["asset"] = "unity.AssetRef<unity.Object>" }),
                    }, null);
                }
                case "asset.copy":
                {
                    var src = folder + "/CopySrc.txt";
                    var g = CreateTextAsset(src, "atf07 copy\n");
                    return (new[]
                    {
                        Step(0, "s", CopyAssetOperation.OperationId, 'q', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["asset"] = Cst("{\"kind\":\"asset\",\"guid\":\"" + g +
                                    "\",\"path\":\"" + src + "\"}"),
                                ["destination"] = Cst("\"" + folder + "/CopyDst.txt\""),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["asset"] = "unity.AssetRef<unity.Object>" }),
                    }, null);
                }
                case "asset.delete":
                {
                    var p = folder + "/Del.txt";
                    var g = CreateTextAsset(p, "atf07 delete\n");
                    CreateTextAsset(folder + "/DelSibling.txt", "sibling\n");
                    return (new[]
                    {
                        Step(0, "s", DeleteAssetOperation.OperationId, 'r', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["asset"] = Cst("{\"kind\":\"asset\",\"guid\":\"" + g +
                                    "\",\"path\":\"" + p + "\"}"),
                            }),
                    }, null);
                }
                case "script.template":
                    EnsureFolder(folder);
                    return (new[]
                    {
                        Step(0, "s", CreateScriptFromTemplateOperation.OperationId, 's',
                            next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["template"] = Cst("\"mono_behaviour\""),
                                ["className"] = Cst("\"Atf07MTemplate\""),
                                ["folder"] = Cst("\"" + folder + "\""),
                            },
                            outputTypes: new Dictionary<string, string>
                            {
                                ["script"] = "unity.AssetRef<unity.MonoScript>",
                                ["typeName"] = "unity.TypeName",
                            }),
                    }, null);
                case "script.create":
                    EnsureFolder(folder);
                    return (new[]
                    {
                        Step(0, "s", CreateScriptOperation.OperationId, 't', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["path"] = Cst("\"" + folder + "/Atf07MCreated.cs\""),
                                ["content"] = new Constant(JsonValue.From(
                                    new JValue("public sealed class Atf07MCreated {}\n"))),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["script"] = "unity.AssetRef<unity.MonoScript>" }),
                    }, null);
                case "package.add":
                    return (new[]
                    {
                        Step(0, "s", UnityPackageAddOperation.OperationId, 'u', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["id"] = new Constant(JsonValue.From(new JValue(dep))),
                            },
                            outputTypes: new Dictionary<string, string>
                                { ["package"] = "core.Json" }),
                    }, null);
                case "package.remove":
                    return (new[]
                    {
                        Step(0, "s", UnityPackageRemoveOperation.OperationId, 'v', next: 1,
                            idempotency: Idempotency.Reconcilable,
                            inputs: new Dictionary<string, InputSource>
                            {
                                ["name"] = Cst("\"" + PkgName + "\""),
                            }),
                    }, null);
            }
            throw new ArgumentOutOfRangeException(nameof(key), key);
        }
    }
}
