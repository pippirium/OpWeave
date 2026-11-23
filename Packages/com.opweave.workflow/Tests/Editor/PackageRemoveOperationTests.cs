#nullable enable

// T-W4.2-05 [Edit] tests: local:T-W4.2-05:package-remove-reconcile — the
// Operation-level half of SPEC §F5's unity.package.remove row over
// injected seams: the name grammar runs at ValidateStatic, at Preflight
// and again immediately before the side effect; Preflight persists the
// name-keyed manifest/lockfile/registered evidence snapshot; Execute
// issues the remove and suspends on the wait token; Poll restores state
// from the token alone; and Reconcile answers Completed only when the
// snapshot proves the package existed and it is absent from the
// manifest and the registered list alike, NotApplied only when the
// state equals the snapshot — re-execution then removes exactly the
// recorded artifact or fails cleanly on a proven-absent one — and
// Unknown for every other state (SPEC §F5's six cases, decided without
// a live Package Manager so every verdict path is exercised). No
// verdict ever issues a remove. The real-UPM halves live in
// PackageRemoveUpmTests.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W4.2-05:package-remove-reconcile — evidence and verdict
    /// coverage for <c>unity.package.remove</c> with every Unity-side
    /// dependency injected.
    /// </summary>
    internal sealed class PackageRemoveOperationTests
    {
        private const string Name = "com.unity.editorcoroutines";
        private const string Sha = "0123456789abcdef0123456789abcdef01234567";
        private const string OtherSha = "fedcba9876543210fedcba9876543210fedcba98";
        private const string GitDep = "file:///fixture/repo.git#" + Sha;
        private const string OtherGitDep = "file:///fixture/repo.git#" + OtherSha;

        // ---------- seams ----------

        private sealed class ScriptedCondition : IWaitCondition
        {
            internal readonly Queue<WaitStatus> Statuses = new Queue<WaitStatus>();
            internal readonly List<JsonValue> Tokens = new List<JsonValue>();

            public WaitStatus Check(JsonValue token)
            {
                Tokens.Add(token);
                return Statuses.Count > 0
                    ? Statuses.Dequeue()
                    : WaitStatus.Pending(token);
            }
        }

        private sealed class StubStaticContext : StaticContext
        {
            private readonly IReadOnlyDictionary<string, InputValue> _parameters;

            internal StubStaticContext(
                string nodeId, IReadOnlyDictionary<string, InputValue> parameters)
            {
                NodeId = nodeId;
                _parameters = parameters;
            }

            public override string NodeId { get; }

            public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

            public override T Get<T>(string port) =>
                throw new NotSupportedException("ValidateStatic reads Parameters only.");
        }

        private static UnityPackageRemoveOperation Op(
            Func<string, JsonValue>? issueRemove = null,
            IWaitCondition? condition = null,
            Func<PackageInstallView>? view = null) =>
            new UnityPackageRemoveOperation(
                issueRemove ?? (name => PackageResolvedCondition.InitialToken(name, false)),
                condition ?? new ScriptedCondition(),
                view ?? (() => EmptyView()));

        private static JsonStepContext Context(
            string? name = null,
            JsonValue? resumeToken = null,
            bool pending = false,
            StepChangeRecorder? changes = null)
        {
            var inputs = new Dictionary<string, InputValue>();
            if (name is not null)
            {
                inputs[UnityPackageRemoveOperation.NamePort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(name)));
            }
            return new JsonStepContext(
                inputs,
                changes ?? new StepChangeRecorder(0, "remove"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                resumeToken,
                pending
                    ? new Dictionary<string, string>
                    {
                        [UnityPackageRemoveOperation.NamePort] = "producer",
                    }
                    : null);
        }

        private static PackageInstallView EmptyView() =>
            new PackageInstallView(null, null, Array.Empty<ResolvedPackageView>());

        private static PackageInstallView View(
            JObject? manifest = null,
            JObject? lockDeps = null,
            params ResolvedPackageView[] registered) =>
            new PackageInstallView(manifest, lockDeps, registered);

        private static PackageInstallView UnreadableView(
            bool manifestRead = true,
            bool lockRead = true,
            bool registeredRead = true) =>
            new PackageInstallView(
                null, null, Array.Empty<ResolvedPackageView>(),
                manifestRead, lockRead, registeredRead);

        private static ResolvedPackageView GitPackage(
            string name,
            string dependency,
            string? commit = null,
            string? requested = null) =>
            new ResolvedPackageView(name, null, "git", dependency, commit, requested);

        private static ResolvedPackageView RegistryPackage(string name, string version) =>
            new ResolvedPackageView(name, version, "registry", version, null, null);

        private static JObject Manifest(string key, string value) =>
            new JObject { [key] = value };

        private static JObject Lock(
            string key, string versionText, string source = "registry") =>
            new JObject
            {
                [key] = new JObject
                {
                    ["version"] = versionText, ["source"] = source,
                },
            };

        // A real Git lock entry: "version" holds the requested
        // dependency text, "hash" the resolved commit and "source" the
        // "git" channel it resolved through (SPEC §G8.1).
        private static JObject GitLock(
            string key, string dep, string? hash, string source = "git")
        {
            var entry = new JObject
            {
                ["version"] = dep, ["source"] = source,
            };
            if (hash != null)
            {
                entry["hash"] = hash;
            }
            return new JObject { [key] = entry };
        }

        private static JsonValue SnapshotFor(
            string name, PackageInstallView preState) =>
            PackageRemoveEvidence.BuildSnapshot(name, preState);

        // The registry install state all three channels agree on.
        private static PackageInstallView Installed(
            string name = Name, string version = "1.0.0") =>
            View(
                manifest: Manifest(name, version),
                lockDeps: Lock(name, version),
                RegistryPackage(name, version));

        // The Git install state all three channels agree on — the §F5
        // row's resolved-source evidence carries the full commit SHA.
        private static PackageInstallView GitInstalled(
            string name = "com.fixture.x",
            string dep = GitDep,
            string? commit = Sha,
            string? requested = Sha) =>
            View(
                manifest: Manifest(name, dep),
                lockDeps: GitLock(name, dep, commit),
                GitPackage(name, dep, commit: commit, requested: requested));

        private static string Describe(ReconcileResult verdict) =>
            verdict switch
            {
                ReconcileResult.Completed _ => "Completed",
                ReconcileResult.NotApplied _ => "NotApplied",
                ReconcileResult.Unknown unknown => "Unknown(" + unknown.Reason + ")",
                _ => verdict?.ToString() ?? "null",
            };

        // ---------- ValidateStatic ----------

        [Test]
        public void ValidateStatic_ValidName_IsClean()
        {
            var op = Op();
            var ctx = new StubStaticContext("remove", new Dictionary<string, InputValue>
            {
                [UnityPackageRemoveOperation.NamePort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(Name))),
            });

            Assert.That(op.ValidateStatic(ctx), Is.Empty,
                "a constant registry package name satisfies the name grammar");
        }

        [Test]
        public void ValidateStatic_NotAPackageName_IsOwOp006_OnTheNamePort()
        {
            var op = Op();
            foreach (var bad in new[]
                     {
                         "com.example.pkg@1.0.0",                 // an id, not a name
                         "https://github.com/org/repo.git",       // a URL
                         "Com.Unity.Pkg",                         // upper-case
                         "com.example..pkg",                      // empty segment
                     })
            {
                var ctx = new StubStaticContext("remove", new Dictionary<string, InputValue>
                {
                    [UnityPackageRemoveOperation.NamePort] =
                        InputValue.Present(JsonValue.From(JValue.CreateString(bad))),
                });

                var findings = op.ValidateStatic(ctx);
                Assert.That(findings.Count, Is.EqualTo(1), "for input: " + bad);
                Assert.That(findings[0].Code, Is.EqualTo("OW-OP-006"), "for input: " + bad);
                Assert.That(findings[0].NodeId, Is.EqualTo("remove"));
                Assert.That(findings[0].Port, Is.EqualTo("name"));
                Assert.That(findings[0].Message, Does.Not.Contain(bad),
                    "the diagnostic never echoes the rejected text");
            }
        }

        [Test]
        public void ValidateStatic_MissingOrNonStringName_ReportsNothing()
        {
            var op = Op();

            Assert.That(
                op.ValidateStatic(new StubStaticContext(
                    "remove", new Dictionary<string, InputValue>())),
                Is.Empty, "a Missing name is the D3 required-input finding");
            Assert.That(
                op.ValidateStatic(new StubStaticContext("remove", new Dictionary<string, InputValue>
                {
                    [UnityPackageRemoveOperation.NamePort] =
                        InputValue.Present(JsonValue.From(new JValue(42))),
                })),
                Is.Empty, "a non-string constant is the codec's finding");
        }

        // ---------- Preflight ----------

        [Test]
        public void Preflight_ValidName_SnapshotCarriesNameAndReadFlags()
        {
            var op = Op();

            var result = op.Preflight(Context(Name));

            Assert.That(result.Diagnostics, Is.Empty);
            var snapshot = (JObject)result.Snapshot.ToToken();
            Assert.That(snapshot["name"]!.ToString(), Is.EqualTo(Name),
                "the §F5 evidence row names the package");
            Assert.That(snapshot["manifest"], Is.Not.Null);
            Assert.That(snapshot["lock"], Is.Not.Null);
            Assert.That(snapshot["registered"], Is.Not.Null);
            var read = (JObject)snapshot["read"]!;
            Assert.That(read["manifest"]!.ToObject<bool>(), Is.True);
            Assert.That(read["lock"]!.ToObject<bool>(), Is.True);
            Assert.That(read["registered"]!.ToObject<bool>(), Is.True);
        }

        [Test]
        public void Preflight_RecordsTargetEvidenceFromAllThreeChannels()
        {
            var view = GitInstalled();
            var op = Op(view: () => view);

            var result = op.Preflight(Context("com.fixture.x"));

            Assert.That(result.Diagnostics, Is.Empty);
            var snapshot = (JObject)result.Snapshot.ToToken();
            Assert.That(
                ((JObject)snapshot["manifest"]!)["com.fixture.x"]!.ToString(),
                Is.EqualTo(GitDep));
            Assert.That(snapshot["lock"] is JObject l &&
                l["com.fixture.x"] is not null, Is.True,
                "the lockfile entry is part of the recorded resolved-source evidence");
            var registered = (JArray)snapshot["registered"]!;
            Assert.That(registered, Has.Count.EqualTo(1));
            Assert.That(registered[0]!["dependency"]!.ToString(), Is.EqualTo(GitDep));
            Assert.That(registered[0]!["resolvedCommit"]!.ToString(), Is.EqualTo(Sha),
                "the §F5 row's resolved source/version — the full Git commit SHA");
            Assert.That(registered[0]!["requestedRevision"]!.ToString(), Is.EqualTo(Sha));
        }

        [Test]
        public void Preflight_OnlyTheTargetNameIsRecorded()
        {
            var view = View(
                manifest: new JObject
                {
                    [Name] = "1.0.0",
                    ["com.other.pkg"] = "2.0.0",
                },
                lockDeps: new JObject
                {
                    [Name] = new JObject
                    {
                        ["version"] = "1.0.0", ["source"] = "registry",
                    },
                    ["com.other.pkg"] = new JObject
                    {
                        ["version"] = "2.0.0", ["source"] = "registry",
                    },
                },
                RegistryPackage(Name, "1.0.0"),
                RegistryPackage("com.other.pkg", "2.0.0"));
            var op = Op(view: () => view);

            var result = op.Preflight(Context(Name));

            var snapshot = (JObject)result.Snapshot.ToToken();
            Assert.That(((JObject)snapshot["manifest"]!).Properties()
                .Select(p => p.Name), Is.EqualTo(new[] { Name }));
            Assert.That(((JObject)snapshot["lock"]!).Properties()
                .Select(p => p.Name), Is.EqualTo(new[] { Name }));
            Assert.That(((JArray)snapshot["registered"]!).Count, Is.EqualTo(1));
        }

        [Test]
        public void Preflight_InvalidName_OwOp006_AndNoEvidence()
        {
            var op = Op();

            var result = op.Preflight(Context("com.example.pkg@1.0.0"));

            Assert.That(result.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-OP-006"));
            Assert.That(result.Diagnostics[0].Port, Is.EqualTo("name"));
            Assert.That(
                ((JObject)result.Snapshot.ToToken()).Count, Is.EqualTo(0),
                "a rejected Preflight persists no reconciliation evidence");
        }

        [Test]
        public void Preflight_PendingName_ProducesNoEvidence()
        {
            var op = Op();

            var result = op.Preflight(Context(pending: true));

            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(((JObject)result.Snapshot.ToToken()).Count, Is.EqualTo(0));
        }

        // ---------- Execute ----------

        [Test]
        public void Execute_ValidName_IssuesRemoveAndWaitsWithTheRequestToken()
        {
            var issued = new List<string>();
            var op = Op(issueRemove: name =>
            {
                issued.Add(name);
                return PackageResolvedCondition.InitialToken(name, false);
            });

            var result = op.Execute(Context(Name));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting));
            Assert.That(issued, Is.EqualTo(new[] { Name }),
                "Execute issues Client.Remove on the verbatim name");
            var token = (JObject)result.ResumeToken!.ToToken();
            Assert.That(token["id"]!.ToString(), Is.EqualTo(Name));
            Assert.That(token["expectPresent"]!.ToObject<bool>(), Is.False,
                "a remove wait arms the absent-membership judgement (SPEC §G4)");
            Assert.That(token["name"]!.ToString(), Is.EqualTo(Name));
            Assert.That(result.WaitingFor, Is.EqualTo("package resolution"));
        }

        [Test]
        public void Execute_InvalidName_FailsBeforeSideEffects_OwOp006()
        {
            var issued = new List<string>();
            var op = Op(issueRemove: name =>
            {
                issued.Add(name);
                return PackageResolvedCondition.InitialToken(name, false);
            });

            var result = op.Execute(Context("https://example.com/repo.git"));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error!.Code, Is.EqualTo("OW-OP-006"));
            Assert.That(result.Error.Port, Is.EqualTo("name"));
            Assert.That(issued, Is.Empty,
                "the name-grammar re-check rejects before Client.Remove runs");
        }

        // ---------- Poll ----------

        [Test]
        public void Poll_Pending_StaysWaitingOnTheToken()
        {
            var condition = new ScriptedCondition();
            var token = PackageResolvedCondition.InitialToken(Name, false);
            condition.Statuses.Enqueue(WaitStatus.Pending(token));
            var op = Op(condition: condition);

            var result = op.Poll(Context(resumeToken: token));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting));
            Assert.That(result.ResumeToken, Is.SameAs(token));
            Assert.That(condition.Tokens, Has.Count.EqualTo(1),
                "Poll restores all state from the persisted resume token");
        }

        [Test]
        public void Poll_FailedRequest_FailsWithOwUnity009_OnTheNamePort()
        {
            var condition = new ScriptedCondition();
            var token = PackageResolvedCondition.InitialToken(Name, false);
            condition.Statuses.Enqueue(WaitStatus.Failed(
                new Diagnostic("OW-UNITY-009", Severity.Error, "the request failed.")));
            var op = Op(condition: condition);

            var result = op.Poll(Context(resumeToken: token));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error!.Code, Is.EqualTo("OW-UNITY-009"),
                "a Package Manager failure is OW-UNITY-009 (SPEC §I.4)");
            Assert.That(result.Error.Port, Is.EqualTo("name"));
        }

        [Test]
        public void Poll_Done_RecordsPackageDeletion_AndSucceeds()
        {
            var condition = new ScriptedCondition();
            condition.Statuses.Enqueue(WaitStatus.Done());
            var changes = new StepChangeRecorder(0, "remove");
            var token = PackageResolvedCondition.InitialToken(Name, false);
            var op = Op(condition: condition);
            var ctx = Context(resumeToken: token, changes: changes);

            var result = op.Poll(ctx);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded));
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Action, Is.EqualTo(ChangeAction.Deleted));
            Assert.That(changes.Records[0].Target.Kind, Is.EqualTo("package"));
            Assert.That(changes.Records[0].Target.Path, Is.EqualTo(Name),
                "the Deleted record names exactly the package the wait was armed for");
            Assert.That(changes.Records[0].Undoable, Is.False,
                "a package removal is not undoable (SPEC §I.4: Undo None)");
        }

        // ---------- Cancel ----------

        [Test]
        public void Cancel_ResumeToken_EndsWaitRequest_WithoutThrowing()
        {
            var token = PackageResolvedCondition.InitialToken(Name, false);
            var op = Op();

            Assert.DoesNotThrow(() => op.Cancel(Context(resumeToken: token)));
            Assert.DoesNotThrow(() => op.Cancel(Context()),
                "Cancel is a no-op when the step never suspended");
        }

        // ---------- Reconcile: SPEC §F5's six cases ----------

        [Test]
        public void Reconcile_F5Case1_NeverExecuted_InstalledIdentically_NotApplied()
        {
            // The step never ran and every channel still shows exactly
            // the recorded package: manifest spec, lockfile entry and the
            // registered identity all equal the snapshot — removal
            // provably not applied, and re-execution removes exactly the
            // recorded artifact (SPEC §F5 row).
            var preState = Installed();
            var snapshot = SnapshotFor(Name, preState);
            var op = Op(view: () => preState);

            Assert.That(
                op.Reconcile(Context(), snapshot),
                Is.InstanceOf<ReconcileResult.NotApplied>());
        }

        [Test]
        public void Reconcile_F5Case1_NeverExecuted_AbsentEverywhere_NotApplied()
        {
            // The step never ran on a package that was never installed:
            // proven absence at capture plus absence everywhere now is
            // the same state — NotApplied; re-running Client.Remove only
            // produces the UPM error, never a mis-delete (SPEC §I.4 row:
            // 既に無いパッケージの削除は Package Manager がエラーにする).
            var snapshot = SnapshotFor(Name, EmptyView());
            var op = Op(view: EmptyView);

            Assert.That(
                op.Reconcile(Context(), snapshot),
                Is.InstanceOf<ReconcileResult.NotApplied>());
        }

        [Test]
        public void Reconcile_F5Case2_RemoveApplied_AbsentEverywhere_Completed()
        {
            // The step ran but StepSucceeded was never recorded: the
            // snapshot proves the package existed and no channel shows it
            // now — the SPEC §F5 row's Completed evidence exactly.
            var snapshot = SnapshotFor(Name, Installed());
            var op = Op(view: EmptyView);

            var verdict = op.Reconcile(Context(), snapshot);

            var completed = verdict as ReconcileResult.Completed;
            Assert.That(completed, Is.Not.Null,
                "recorded presence + absence everywhere is Completed — " +
                "got " + Describe(verdict));
            Assert.That(completed!.Outputs, Is.Empty,
                "unity.package.remove declares no outputs (SPEC §I.4)");
        }

        [Test]
        public void Reconcile_RecordedManifestOnly_AbsentNow_Unknown()
        {
            // The manifest declared the dependency but no channel proves
            // the package ever resolved — a manifest-only record lacks
            // the §F5 row's "resolved source/version" half, so it never
            // established the installed identity and absence everywhere
            // cannot be attributed to the step.
            var snapshot = SnapshotFor(
                Name, View(manifest: Manifest(Name, "1.0.0")));
            var op = Op(view: EmptyView);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null,
                "a manifest-only recorded state is unproven identity — " +
                "got " + Describe(verdict));
            Assert.That(unknown!.Reason,
                Is.EqualTo(PackageRemoveEvidence.RecordedStateInconsistent));
        }

        [Test]
        public void Reconcile_F5Case3_ManifestCleared_StillRegistered_Unknown()
        {
            // Partial side effect: the manifest entry is gone but the
            // lockfile and the registered list still hold the recorded
            // package — the SPEC §F5 row's disagreement case, never
            // Completed and never an automatic re-remove.
            var snapshot = SnapshotFor(Name, Installed());
            var now = View(
                null, Lock(Name, "1.0.0"), RegistryPackage(Name, "1.0.0"));
            var op = Op(view: () => now);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null,
                "manifest and registered list disagree — got " + Describe(verdict));
            Assert.That(unknown!.Reason, Is.EqualTo(PackageRemoveEvidence.Partial));
        }

        [Test]
        public void Reconcile_F5Case3_RegisteredGone_ManifestAndLockRemain_Unknown()
        {
            // The reverse half: the registered list dropped the package
            // while the manifest and lockfile still declare it — another
            // form of the row's manifest/registered disagreement.
            var snapshot = SnapshotFor(Name, Installed());
            var now = View(
                manifest: Manifest(Name, "1.0.0"),
                lockDeps: Lock(Name, "1.0.0"));
            var op = Op(view: () => now);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageRemoveEvidence.Partial));
        }

        [Test]
        public void Reconcile_F5Case3_LockCleared_ManifestAndRegisteredRemain_Unknown()
        {
            // A lockfile trace that vanished while the manifest and the
            // registered list still hold the package is channel
            // disagreement — the possibly-partial state the row lists.
            var snapshot = SnapshotFor(Name, Installed());
            var now = View(
                manifest: Manifest(Name, "1.0.0"),
                lockDeps: null,
                RegistryPackage(Name, "1.0.0"));
            var op = Op(view: () => now);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageRemoveEvidence.Partial));
        }

        [Test]
        public void Reconcile_F5Case4_RecordedAbsent_PackageAppeared_Unknown()
        {
            // The snapshot proved the name absent; a package is installed
            // now. It cannot be attributed to a removal that never ran —
            // and an automatic re-run would delete an artifact this run
            // never targeted, the §F5 same-name mis-delete the verdict
            // exists to prevent.
            var snapshot = SnapshotFor(Name, EmptyView());
            var op = Op(view: () => Installed());

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageRemoveEvidence.Appeared));
        }

        [Test]
        public void Reconcile_F5Case4_SameNameDifferentVersion_Unknown()
        {
            // The recorded 1.0.0 install is not the 2.0.0 install the
            // channels show now — a same-name artifact that is not the
            // recorded one (§F5 case 4/5 boundary).
            var snapshot = SnapshotFor(Name, Installed());
            var now = Installed(version: "2.0.0");
            var op = Op(view: () => now);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "a same-name package at another version is not the " +
                "recorded artifact — got " + Describe(verdict));
        }

        [Test]
        public void Reconcile_F5Case5_ManifestSpecChanged_Unknown()
        {
            // Every channel still has the package but the manifest's
            // spec text changed since the snapshot — another operation
            // touched it; the removal's effect is unprovable.
            var snapshot = SnapshotFor(Name, Installed());
            var now = View(
                manifest: Manifest(Name, "2.0.0"),
                lockDeps: Lock(Name, "1.0.0"),
                RegistryPackage(Name, "1.0.0"));
            var op = Op(view: () => now);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageRemoveEvidence.Changed));
        }

        [Test]
        public void Reconcile_F5Case5_GitCommitChanged_Unknown()
        {
            // The recorded Git install carried commit Sha; the same name
            // now resolves to OtherSha — not the recorded artifact, so
            // the removal's state cannot be proven either way (the §F5
            // row's full-commit evidence is exactly what disproves
            // sameness).
            var snapshot = SnapshotFor("com.fixture.x", GitInstalled());
            var now = GitInstalled(dep: OtherGitDep, commit: OtherSha, requested: OtherSha);
            var op = Op(view: () => now);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "a different resolved commit is not the recorded " +
                "artifact — got " + Describe(verdict));
        }

        [Test]
        public void Reconcile_GitPackage_IdenticalFullCommit_NotApplied()
        {
            // Same name, same dependency text, same full resolved commit
            // SHA — the §F5 row's Git evidence proves the recorded
            // artifact is still installed: removal provably not applied.
            var preState = GitInstalled();
            var snapshot = SnapshotFor("com.fixture.x", preState);
            var op = Op(view: () => GitInstalled());

            Assert.That(
                op.Reconcile(Context(), snapshot),
                Is.InstanceOf<ReconcileResult.NotApplied>());
        }

        [Test]
        public void Reconcile_GitPackage_WithoutCommitEvidence_Unknown()
        {
            // A Git-source registered entry with no resolved commit on
            // either side cannot prove artifact identity — sameness is
            // unproven even though the recorded fields match (SPEC §F5:
            // Git なら完全なコミット SHA).
            var preState = GitInstalled(commit: null, requested: "vtag");
            var snapshot = SnapshotFor("com.fixture.x", preState);
            var now = GitInstalled(commit: null, requested: "vtag");
            var op = Op(view: () => now);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "unprovable Git identity is never NotApplied — got " +
                Describe(verdict));
        }

        [Test]
        public void Reconcile_F5Case6_VerdictIsSessionIndependent()
        {
            // The §F5 verdict is a pure function of the persisted
            // snapshot and the package state — an Editor restart swaps
            // the session, the request handle is gone and a fresh
            // Operation instance must reach the identical verdict
            // (SPEC §F5 case 6; the real-restart half is
            // PackageRemoveUpmTests' domain-reload crossing).
            var snapshot = SnapshotFor(Name, Installed());

            var first = Op(view: EmptyView).Reconcile(Context(), snapshot);
            var second = Op(view: EmptyView).Reconcile(Context(), snapshot);

            Assert.That(first, Is.InstanceOf<ReconcileResult.Completed>());
            Assert.That(second, Is.InstanceOf<ReconcileResult.Completed>(),
                "a fresh Operation on persisted evidence alone reaches " +
                "the same verdict across a restart");
        }

        // ---------- Reconcile: recorded identity integrity ----------

        [Test]
        public void Reconcile_RecordedUnreadChannel_AbsentNow_Unknown()
        {
            // The preflight capture could not read the registered list,
            // so the manifest/lockfile entries it did record are a
            // partial state — they never proved the package existed
            // whole, and absence everywhere now cannot be attributed to
            // the removal (SPEC §F5: unread evidence can never prove a
            // removal).
            var preState = new PackageInstallView(
                Manifest(Name, "1.0.0"),
                Lock(Name, "1.0.0"),
                Array.Empty<ResolvedPackageView>(),
                manifestRead: true, lockRead: true, registeredRead: false);
            var snapshot = SnapshotFor(Name, preState);
            var op = Op(view: EmptyView);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null,
                "recorded presence under an unread channel is unproven — " +
                "got " + Describe(verdict));
            Assert.That(unknown!.Reason,
                Is.EqualTo(PackageRemoveEvidence.RecordedStateIncomplete));
        }

        [Test]
        public void Reconcile_GitWithoutFullSha_AbsentNow_Unknown()
        {
            // A Git recorded state whose resolved commit is not the
            // persisted full SHA never established the artifact's
            // identity — absence everywhere now cannot be attributed to
            // the removal (SPEC §F5 row: Git なら完全なコミット SHA).
            var snapshot = SnapshotFor(
                "com.fixture.x", GitInstalled(commit: null, requested: "vtag"));
            var op = Op(view: EmptyView);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "a Git pre-state without a full commit SHA proves no " +
                "identity — got " + Describe(verdict));
        }

        [Test]
        public void Reconcile_RecordedManifestOnly_SameNow_Unknown()
        {
            // A preflight that recorded only the manifest entry never
            // established the §F5 row's identity — the resolved
            // source/version half is missing — so the same manifest-only
            // state observed again is unproven identity, not "same as
            // snapshot".
            var state = View(manifest: Manifest(Name, "1.0.0"));
            var snapshot = SnapshotFor(Name, state);
            var op = Op(view: () => state);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "a manifest-only recorded state is unproven identity — " +
                "got " + Describe(verdict));
        }

        [Test]
        public void Reconcile_RecordedInconsistent_SameNow_Unknown()
        {
            // The recorded channels disagree — the lockfile resolved
            // "1.0.0" while the registered list holds "2.0.0" — so the
            // snapshot never established one artifact identity. Each
            // channel matching its own snapshot does not make the state
            // consistent: the same disagreement observed again stays
            // Unknown, never NotApplied.
            var state = View(
                manifest: Manifest(Name, "1.0.0"),
                lockDeps: Lock(Name, "1.0.0"),
                RegistryPackage(Name, "2.0.0"));
            var snapshot = SnapshotFor(Name, state);
            var op = Op(view: () => state);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "identical-but-inconsistent channels are unproven " +
                "identity — got " + Describe(verdict));
        }

        [Test]
        public void Reconcile_RecordedLockSourceMismatch_Unknown()
        {
            // The lockfile recorded the package under a different source
            // than the registered list — the recorded channels disagree
            // about the artifact's origin, so no installed identity was
            // ever established even though the same entries are still
            // observed.
            var state = View(
                manifest: Manifest(Name, "1.0.0"),
                lockDeps: Lock(Name, "1.0.0", source: "local"),
                RegistryPackage(Name, "1.0.0"));
            var snapshot = SnapshotFor(Name, state);
            var op = Op(view: () => state);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "a lock/registered source mismatch is unproven " +
                "identity — got " + Describe(verdict));
        }

        // ---------- Reconcile: evidence integrity ----------

        [Test]
        public void Reconcile_MalformedSnapshot_Unknown()
        {
            var op = Op(view: EmptyView);

            foreach (var snapshot in new[]
                     {
                         JsonValue.From(new JObject()),
                         JsonValue.From(new JObject { ["name"] = 42 }),
                         JsonValue.From(new JObject { ["name"] = "" }),
                     })
            {
                Assert.That(
                    op.Reconcile(Context(), snapshot),
                    Is.InstanceOf<ReconcileResult.Unknown>(),
                    "a malformed snapshot is never a verdict basis");
            }
        }

        [Test]
        public void Reconcile_AbsentLookingButUnreadChannel_Unknown()
        {
            // Absence cannot be proven while a channel cannot be read:
            // the recorded install state plus an unread manifest is
            // Unknown, never Completed (SPEC §F5: unread evidence can
            // never prove a removal).
            var snapshot = SnapshotFor(Name, Installed());
            var op = Op(view: () => UnreadableView(manifestRead: false));

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason,
                Is.EqualTo(PackageRemoveEvidence.EvidenceUnreadable));
        }

        [Test]
        public void Reconcile_PresentButUnreadChannel_Unknown()
        {
            // The same rule on the presence side: an unread registered
            // list can neither confirm the recorded artifact nor prove
            // the rest of the state.
            var snapshot = SnapshotFor(Name, Installed());
            var now = new PackageInstallView(
                Manifest(Name, "1.0.0"),
                Lock(Name, "1.0.0"),
                Array.Empty<ResolvedPackageView>(),
                manifestRead: true, lockRead: true, registeredRead: false);
            var op = Op(view: () => now);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason,
                Is.EqualTo(PackageRemoveEvidence.EvidenceUnreadable));
        }

        [Test]
        public void Reconcile_RecordedUnreadChannel_Unknown()
        {
            // The preflight capture could not read the registered list,
            // so the recorded state is incomplete — "same as snapshot"
            // is unprovable and the recorded absence cannot be proven
            // either. Unknown, never NotApplied.
            var preState = new PackageInstallView(
                Manifest(Name, "1.0.0"),
                Lock(Name, "1.0.0"),
                Array.Empty<ResolvedPackageView>(),
                manifestRead: true, lockRead: true, registeredRead: false);
            var snapshot = SnapshotFor(Name, preState);
            var op = Op(view: () => Installed());

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason,
                Is.EqualTo(PackageRemoveEvidence.RecordedStateIncomplete));
        }

        [Test]
        public void Reconcile_RecordedAbsentUnread_Unknown()
        {
            // The snapshot recorded nothing for the name but one channel
            // was unread at capture — the recorded absence was never
            // proven, so current absence is not "same as snapshot" and
            // not proven removal.
            var preState = UnreadableView(registeredRead: false);
            var snapshot = SnapshotFor(Name, preState);
            var op = Op(view: EmptyView);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason,
                Is.EqualTo(PackageRemoveEvidence.RecordedStateIncomplete));
        }

        [Test]
        public void Reconcile_DuplicateRegisteredNames_Unknown()
        {
            // Two registered packages under one name cannot be the one
            // recorded artifact — state the list itself makes ambiguous.
            var snapshot = SnapshotFor(Name, Installed());
            var now = View(
                manifest: Manifest(Name, "1.0.0"),
                lockDeps: Lock(Name, "1.0.0"),
                RegistryPackage(Name, "1.0.0"),
                RegistryPackage(Name, "2.0.0"));
            var op = Op(view: () => now);

            Assert.That(
                op.Reconcile(Context(), snapshot),
                Is.InstanceOf<ReconcileResult.Unknown>());
        }

        [Test]
        public void Reconcile_NeverIssuesARemove_InAnyVerdict()
        {
            // Reconciliation only reads: whatever the verdict, no
            // verdict path may issue Client.Remove — Unknown is the
            // NeedsReview answer, never an automatic re-remove, and a
            // mis-delete of an unrecorded artifact is exactly what the
            // same-name rule forbids (SPEC §F5).
            var issued = new List<string>();
            Func<string, JsonValue> remove = name =>
            {
                issued.Add(name);
                return PackageResolvedCondition.InitialToken(name, false);
            };

            var states = new (string Label, PackageInstallView Before, PackageInstallView Now)[]
            {
                ("Completed", Installed(), EmptyView()),
                ("NotApplied-installed", Installed(), Installed()),
                ("NotApplied-absent", EmptyView(), EmptyView()),
                ("Unknown-partial", Installed(), View(null, null, RegistryPackage(Name, "1.0.0"))),
                ("Unknown-appeared", EmptyView(), Installed()),
                ("Unknown-changed", Installed(), Installed(version: "2.0.0")),
            };
            foreach (var (label, before, now) in states)
            {
                var snapshot = SnapshotFor(Name, before);
                var op = Op(issueRemove: remove, view: () => now);
                op.Reconcile(Context(), snapshot);
                Assert.That(issued, Is.Empty, "Reconcile removed a package: " + label);
            }
        }
    }
}
