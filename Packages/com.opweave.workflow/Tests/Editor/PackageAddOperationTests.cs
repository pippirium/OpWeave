#nullable enable

// T-W4.2-04 [Edit] tests: local:T-W4.2-04:package-add-reconcile — the
// Operation-level half of SPEC §G8.1 over injected seams: the SG-19
// allowlist runs at ValidateStatic, at Preflight and again immediately
// before the side effect (§G8 item 6); Preflight persists the §G8.1
// evidence snapshot; Execute issues the add and suspends on the wait
// token; Poll restores state from the token alone; and Reconcile answers
// Completed only on the full identity row — registry name+version or Git
// origin+subPath+requested revision+full resolved commit SHA —
// NotApplied only on provable absence, Unknown for everything else
// (SPEC §F5's six cases and §G8.1 tests 1-4 and 9, decided without a live
// Package Manager so every verdict path is exercised). The real-UPM
// halves live in PackageAddUpmTests.
using System;
using System.Collections.Generic;
using System.IO;
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
    /// local:T-W4.2-04:package-add-reconcile — policy, evidence and
    /// verdict coverage for <c>unity.package.add</c> with every Unity-side
    /// dependency injected.
    /// </summary>
    internal sealed class PackageAddOperationTests
    {
        private const string RegistryId = "com.unity.editorcoroutines@1.0.0";
        private const string GitRepo = "https://github.com/org/repo.git";
        private const string Sha = "0123456789abcdef0123456789abcdef01234567";
        private const string OtherSha = "fedcba9876543210fedcba9876543210fedcba98";

        // ---------- seams ----------

        private sealed class ScriptedCondition : IWaitCondition
        {
            internal readonly Queue<WaitStatus> Statuses = new Queue<WaitStatus>();
            internal readonly List<JsonValue> Tokens = new List<JsonValue>();

            public WaitStatus Check(JsonValue token)
            {
                Tokens.Add(token);
                return Statuses.Count > 0 ? Statuses.Dequeue() : WaitStatus.Pending(token);
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

        private static UnityPackageAddOperation Op(
            OpWeaveSettings? settings = null,
            Func<string, JsonValue>? issueAdd = null,
            IWaitCondition? condition = null,
            Func<PackageInstallView>? view = null) =>
            new UnityPackageAddOperation(
                () => Result<OpWeaveSettings>.Success(settings ?? OpWeaveSettings.Default),
                issueAdd ?? (id => PackageResolvedCondition.InitialToken(id, true)),
                condition ?? new ScriptedCondition(),
                view ?? (() => EmptyView()));

        private static OpWeaveSettings SettingsWithSources(params string[] sources)
        {
            var parsed = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"allowedPackageSources\":[" +
                string.Join(",", sources.Select(s => "\"" + s + "\"")) + "]}");
            Assert.That(parsed.Ok, Is.True, () => string.Join(
                "; ", parsed.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return parsed.Value!;
        }

        private static UnityPackageAddOperation OpWithFailingSettings(Diagnostic diagnostic) =>
            new UnityPackageAddOperation(
                () => Result<OpWeaveSettings>.Failure(new[] { diagnostic }),
                id => PackageResolvedCondition.InitialToken(id, true),
                new ScriptedCondition(),
                EmptyView);

        private static JsonStepContext Context(
            string? id = null,
            JsonValue? resumeToken = null,
            bool pending = false,
            StepChangeRecorder? changes = null)
        {
            var inputs = new Dictionary<string, InputValue>();
            if (id is not null)
            {
                inputs[UnityPackageAddOperation.IdPort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(id)));
            }
            return new JsonStepContext(
                inputs,
                changes ?? new StepChangeRecorder(0, "add"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                resumeToken,
                pending
                    ? new Dictionary<string, string>
                    {
                        [UnityPackageAddOperation.IdPort] = "producer",
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

        private static string TempPackagesDir()
        {
            var dir = Path.Combine(
                Path.GetTempPath(),
                "OpWeave.PackageAdd." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static ResolvedPackageView GitPackage(
            string name,
            string dependency,
            string? commit = null,
            string? requested = null,
            string version = "1.0.0") =>
            new ResolvedPackageView(name, version, "git", dependency, commit, requested);

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
            string key, string dep, string hash, string source = "git") =>
            new JObject
            {
                [key] = new JObject
                {
                    ["version"] = dep, ["hash"] = hash, ["source"] = source,
                },
            };

        private static JsonValue SnapshotFor(
            string id, PackageInstallView preState)
        {
            Assert.That(PackageAddSource.TryParse(id, out var source), Is.True,
                "the fixture id must parse: " + id);
            return PackageAddEvidence.BuildSnapshot(id, source!, preState);
        }

        private static ReconcileResult JudgeFor(string id, PackageInstallView now) =>
            PackageAddEvidence.Judge(SnapshotFor(id, EmptyView()), now);

        // ---------- ValidateStatic ----------

        [Test]
        public void ValidateStatic_AllowedRegistryId_IsClean()
        {
            var op = Op();
            var ctx = new StubStaticContext("add", new Dictionary<string, InputValue>
            {
                [UnityPackageAddOperation.IdPort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(RegistryId))),
            });

            Assert.That(op.ValidateStatic(ctx), Is.Empty,
                "the default registry:com.unity.* rule allows the constant");
        }

        [Test]
        public void ValidateStatic_DisallowedRegistryId_IsOwPerm001()
        {
            var op = Op();
            var ctx = new StubStaticContext("add", new Dictionary<string, InputValue>
            {
                [UnityPackageAddOperation.IdPort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString("com.example.pkg@1.0.0"))),
            });

            var findings = op.ValidateStatic(ctx);
            Assert.That(findings.Count, Is.EqualTo(1));
            Assert.That(findings[0].Code, Is.EqualTo("OW-PERM-001"));
            Assert.That(findings[0].NodeId, Is.EqualTo("add"));
            Assert.That(findings[0].Port, Is.EqualTo("id"));
        }

        [Test]
        public void ValidateStatic_GitIdAgainstDefaultAllowlist_IsOwPerm001()
        {
            var op = Op();
            var ctx = new StubStaticContext("add", new Dictionary<string, InputValue>
            {
                [UnityPackageAddOperation.IdPort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(GitRepo))),
            });

            var findings = op.ValidateStatic(ctx);
            Assert.That(findings.Count, Is.EqualTo(1));
            Assert.That(findings[0].Code, Is.EqualTo("OW-PERM-001"));
        }

        [Test]
        public void ValidateStatic_GitIdAllowedBySg19Rule_IsClean_WithSchemeHostNormalization()
        {
            // SG-19: the candidate's scheme and host ASCII-lowercase to the
            // allowed origin; path case and the revision are preserved.
            var op = Op(SettingsWithSources("git:" + GitRepo));
            var ctx = new StubStaticContext("add", new Dictionary<string, InputValue>
            {
                [UnityPackageAddOperation.IdPort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(
                        "HTTPS://GITHUB.COM/org/repo.git#" + Sha))),
            });

            Assert.That(op.ValidateStatic(ctx), Is.Empty);
        }

        [Test]
        public void ValidateStatic_GitScpIdAllowedBySg19Rule_IsClean()
        {
            var op = Op(SettingsWithSources("git:git@github.com:org/repo.git"));
            var ctx = new StubStaticContext("add", new Dictionary<string, InputValue>
            {
                [UnityPackageAddOperation.IdPort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(
                        "git@GITHUB.com:org/repo.git"))),
            });

            Assert.That(op.ValidateStatic(ctx), Is.Empty,
                "the SCP host ASCII-lowercases for the allowlist match");
        }

        [Test]
        public void ValidateStatic_CredentialId_ReportsNothing_D6OwnsOwOp006()
        {
            var op = Op(SettingsWithSources("git:" + GitRepo));
            var ctx = new StubStaticContext("add", new Dictionary<string, InputValue>
            {
                [UnityPackageAddOperation.IdPort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(
                        "https://user:pass@github.com/org/repo.git"))),
            });

            Assert.That(op.ValidateStatic(ctx), Is.Empty,
                "the credential finding is the D6 phase's OW-OP-006, so " +
                "ValidateStatic must not double-report the port");
        }

        [Test]
        public void ValidateStatic_MissingOrNonStringId_ReportsNothing()
        {
            var op = Op();

            Assert.That(
                op.ValidateStatic(new StubStaticContext(
                    "add", new Dictionary<string, InputValue>())),
                Is.Empty, "a Missing id is the D3 required-input finding");
            Assert.That(
                op.ValidateStatic(new StubStaticContext("add", new Dictionary<string, InputValue>
                {
                    [UnityPackageAddOperation.IdPort] =
                        InputValue.Present(JsonValue.From(new JValue(42))),
                })),
                Is.Empty, "a non-string constant is the codec's finding");
        }

        [Test]
        public void ValidateStatic_SettingsLoadFailure_SurfacesOwCfg()
        {
            var op = OpWithFailingSettings(
                new Diagnostic("OW-CFG-002", Severity.Error, "settings broken"));
            var ctx = new StubStaticContext("add", new Dictionary<string, InputValue>
            {
                [UnityPackageAddOperation.IdPort] =
                    InputValue.Present(JsonValue.From(JValue.CreateString(RegistryId))),
            });

            var findings = op.ValidateStatic(ctx);
            Assert.That(findings.Count, Is.EqualTo(1));
            Assert.That(findings[0].Code, Is.EqualTo("OW-CFG-002"));
        }

        // ---------- Preflight ----------

        [Test]
        public void Preflight_GitShaId_SnapshotCarriesExpectedCommitAndIdentity()
        {
            var op = Op(SettingsWithSources("git:" + GitRepo));
            var id = GitRepo + "#" + Sha;

            var result = op.Preflight(Context(id));

            Assert.That(result.Diagnostics, Is.Empty);
            var snapshot = result.Snapshot.ToToken() as JObject;
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!["kind"]!.ToString(), Is.EqualTo("git"));
            Assert.That(snapshot["id"]!.ToString(), Is.EqualTo(id));
            Assert.That(snapshot["repositoryUrl"]!.ToString(), Is.EqualTo(GitRepo));
            Assert.That(snapshot["requestedRevision"]!.ToString(), Is.EqualTo(Sha));
            Assert.That(snapshot["expectedCommit"]!.ToString(), Is.EqualTo(Sha),
                "a full-SHA request names its expected commit (SPEC §G8.1)");
        }

        [Test]
        public void Preflight_GitTagId_SnapshotHasNoExpectedCommit()
        {
            var op = Op(SettingsWithSources("git:" + GitRepo));
            var id = GitRepo + "?path=/pkg#vtag";

            var result = op.Preflight(Context(id));

            Assert.That(result.Diagnostics, Is.Empty);
            var snapshot = (JObject)result.Snapshot.ToToken();
            Assert.That(snapshot["requestedRevision"]!.ToString(), Is.EqualTo("vtag"));
            Assert.That(snapshot["packageSubPath"]!.ToString(), Is.EqualTo("/pkg"));
            Assert.That(snapshot["expectedCommit"], Is.Null,
                "a tag has no side-effect-free expected commit to persist");
        }

        [Test]
        public void Preflight_RegistryId_SnapshotCarriesNameAndVersion()
        {
            var op = Op();

            var result = op.Preflight(Context(RegistryId));

            Assert.That(result.Diagnostics, Is.Empty);
            var snapshot = (JObject)result.Snapshot.ToToken();
            Assert.That(snapshot["kind"]!.ToString(), Is.EqualTo("registry"));
            Assert.That(snapshot["packageName"]!.ToString(),
                Is.EqualTo("com.unity.editorcoroutines"));
            Assert.That(snapshot["version"]!.ToString(), Is.EqualTo("1.0.0"));
        }

        [Test]
        public void Preflight_RecordsTargetEvidenceFromAllThreeChannels()
        {
            var dep = GitRepo + "#" + Sha;
            var view = View(
                manifest: Manifest("com.example.pkg", dep),
                lockDeps: GitLock("com.example.pkg", dep, OtherSha),
                GitPackage("com.example.pkg", dep, commit: OtherSha, requested: "old"));
            var op = Op(SettingsWithSources("git:" + GitRepo), view: () => view);

            var result = op.Preflight(Context(dep));

            Assert.That(result.Diagnostics, Is.Empty);
            var snapshot = (JObject)result.Snapshot.ToToken();
            Assert.That(
                ((JObject)snapshot["manifest"]!)["com.example.pkg"]!.ToString(),
                Is.EqualTo(dep));
            Assert.That(snapshot["lock"] is JObject l &&
                l["com.example.pkg"] is not null, Is.True);
            var registered = (JArray)snapshot["registered"]!;
            Assert.That(registered, Has.Count.EqualTo(1));
            Assert.That(registered[0]!["resolvedCommit"]!.ToString(), Is.EqualTo(OtherSha));
        }

        [Test]
        public void Preflight_DisallowedId_FailsWithOwPerm001_AndNoEvidence()
        {
            var op = Op();

            var result = op.Preflight(Context("com.example.pkg@1.0.0"));

            Assert.That(result.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(result.Diagnostics[0].Code, Is.EqualTo("OW-PERM-001"));
            Assert.That(
                ((JObject)result.Snapshot.ToToken()).Count, Is.EqualTo(0),
                "a rejected Preflight persists no reconciliation evidence");
        }

        [Test]
        public void Preflight_PendingId_ProducesNoEvidence()
        {
            var op = Op();

            var result = op.Preflight(Context(pending: true));

            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(((JObject)result.Snapshot.ToToken()).Count, Is.EqualTo(0));
        }

        // ---------- Execute ----------

        [Test]
        public void Execute_AllowedId_IssuesAddAndWaitsWithTheRequestToken()
        {
            var issued = new List<string>();
            var op = Op(
                issueAdd: id =>
                {
                    issued.Add(id);
                    return PackageResolvedCondition.InitialToken(id, true);
                });

            var result = op.Execute(Context(RegistryId));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting));
            Assert.That(issued, Is.EqualTo(new[] { RegistryId }),
                "Execute issues Client.Add on the verbatim id");
            var token = (JObject)result.ResumeToken!.ToToken();
            Assert.That(token["id"]!.ToString(), Is.EqualTo(RegistryId));
            Assert.That(token["expectPresent"]!.ToObject<bool>(), Is.True);
            Assert.That(result.WaitingFor, Is.EqualTo("package resolution"));
        }

        [Test]
        public void Execute_DisallowedId_FailsBeforeSideEffects_OwPerm001()
        {
            var issued = new List<string>();
            var op = Op(issueAdd: id =>
            {
                issued.Add(id);
                return PackageResolvedCondition.InitialToken(id, true);
            });

            var result = op.Execute(Context("com.example.pkg@1.0.0"));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error!.Code, Is.EqualTo("OW-PERM-001"));
            Assert.That(issued, Is.Empty,
                "the §G8 item 6 re-check rejects before Client.Add runs");
        }

        [Test]
        public void Execute_CredentialId_FailsBeforeSideEffects_OwOp006()
        {
            var issued = new List<string>();
            var op = Op(issueAdd: id =>
            {
                issued.Add(id);
                return PackageResolvedCondition.InitialToken(id, true);
            });

            var result = op.Execute(Context("https://user:pass@github.com/org/repo.git"));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error!.Code, Is.EqualTo("OW-OP-006"));
            Assert.That(result.Error.Message, Does.Not.Contain("user:pass"),
                "the diagnostic never echoes the credential text (SPEC §G8.1)");
            Assert.That(issued, Is.Empty);
        }

        // ---------- Poll ----------

        [Test]
        public void Poll_Pending_StaysWaitingOnTheToken()
        {
            var condition = new ScriptedCondition();
            var token = PackageResolvedCondition.InitialToken(RegistryId, true);
            condition.Statuses.Enqueue(WaitStatus.Pending(token));
            var op = Op(condition: condition);

            var result = op.Poll(Context(resumeToken: token));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting));
            Assert.That(result.ResumeToken, Is.SameAs(token));
            Assert.That(condition.Tokens, Has.Count.EqualTo(1),
                "Poll restores all state from the persisted resume token");
        }

        [Test]
        public void Poll_FailedRequest_FailsWithTheDiagnostic_OnTheIdPort()
        {
            var condition = new ScriptedCondition();
            var token = PackageResolvedCondition.InitialToken(RegistryId, true);
            condition.Statuses.Enqueue(WaitStatus.Failed(
                new Diagnostic("OW-UNITY-009", Severity.Error, "the request failed.")));
            var op = Op(condition: condition);

            var result = op.Poll(Context(resumeToken: token));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Failed));
            Assert.That(result.Error!.Code, Is.EqualTo("OW-UNITY-009"));
            Assert.That(result.Error.Port, Is.EqualTo("id"));
        }

        [Test]
        public void Poll_Done_ResolvesRegistryPackage_SetsOutputAndChange()
        {
            var dep = "com.unity.editorcoroutines@1.0.0";
            var view = View(registered: RegistryPackage(
                "com.unity.editorcoroutines", "1.0.0"));
            var condition = new ScriptedCondition();
            condition.Statuses.Enqueue(WaitStatus.Done());
            var changes = new StepChangeRecorder(0, "add");
            var token = PackageResolvedCondition.InitialToken(dep, true);
            var op = Op(condition: condition, view: () => view);
            var ctx = Context(resumeToken: token, changes: changes);

            var result = op.Poll(ctx);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded));
            var output = (JObject)ctx.Outputs[UnityPackageAddOperation.PackagePort].ToToken();
            Assert.That(output["name"]!.ToString(), Is.EqualTo("com.unity.editorcoroutines"));
            Assert.That(output["version"]!.ToString(), Is.EqualTo("1.0.0"));
            Assert.That(output["source"]!.ToString(), Is.EqualTo("registry"));
            Assert.That(changes.Records, Has.Count.EqualTo(1));
            Assert.That(changes.Records[0].Target.Kind, Is.EqualTo("package"));
            Assert.That(
                changes.Records[0].Target.Path, Is.EqualTo("com.unity.editorcoroutines"));
        }

        [Test]
        public void Poll_Done_GitPackageResolvesBySourceIdentity()
        {
            var dep = "file:///fixture/repo.git?path=/pkgs/x#" + Sha;
            var view = View(registered: GitPackage(
                "com.fixture.x", dep, commit: Sha, requested: Sha));
            var condition = new ScriptedCondition();
            condition.Statuses.Enqueue(WaitStatus.Done());
            var token = PackageResolvedCondition.InitialToken(dep, true);
            var op = Op(condition: condition, view: () => view);
            var ctx = Context(resumeToken: token);

            var result = op.Poll(ctx);

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Succeeded));
            var output = (JObject)ctx
                .Outputs[UnityPackageAddOperation.PackagePort].ToToken();
            Assert.That(output["name"]!.ToString(), Is.EqualTo("com.fixture.x"));
            Assert.That(output["source"]!.ToString(), Is.EqualTo("git"),
                "a Git package resolves by source identity, never by name alone");
        }

        [Test]
        public void Poll_Done_Unattributable_StaysWaiting()
        {
            // The condition reported presence but the registered list shows
            // no package tied to this request's source — keep waiting rather
            // than guess (SPEC §G8.1 identity-by-source).
            var view = View(registered: RegistryPackage("com.other.pkg", "2.0.0"));
            var condition = new ScriptedCondition();
            condition.Statuses.Enqueue(WaitStatus.Done());
            var token = PackageResolvedCondition.InitialToken(RegistryId, true);
            var op = Op(condition: condition, view: () => view);

            var result = op.Poll(Context(resumeToken: token));

            Assert.That(result.Kind, Is.EqualTo(StepResultKind.Waiting));
        }

        // ---------- Reconcile: SPEC §F5's six cases + §G8.1 tests ----------

        [Test]
        public void Reconcile_F5Case1_NeverExecuted_AbsentEverywhere_NotApplied()
        {
            var dep = "file:///fixture/repo.git#" + Sha;
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: EmptyView);

            Assert.That(
                op.Reconcile(Context(), snapshot), Is.InstanceOf<ReconcileResult.NotApplied>());
        }

        [Test]
        public void Reconcile_GitShaInstall_ExactMatch_Completed()
        {
            // SPEC §G8.1 test 1 / F5 case 2: the step ran, StepSucceeded was
            // not recorded, and the full evidence row proves the same
            // package — origin, subPath, requested revision and resolved
            // full commit SHA.
            var dep = "file:///fixture/repo.git?path=/pkgs/x#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var completed = verdict as ReconcileResult.Completed;
            Assert.That(completed, Is.Not.Null,
                "the full §G8.1 evidence row must reconcile Completed");
            var output = (JObject)completed!
                .Outputs[UnityPackageAddOperation.PackagePort].ToToken();
            Assert.That(output["name"]!.ToString(), Is.EqualTo("com.fixture.x"));
            Assert.That(output["source"]!.ToString(), Is.EqualTo("git"));
        }

        [Test]
        public void Reconcile_GitTagRequest_SamePackageUnknown_NoExpectedCommit()
        {
            // SPEC §G8.1 test 2: a tag request persists no expected commit,
            // so a same-name / same-origin package can never be Completed.
            var dep = "file:///fixture/repo.git#vtag";
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: "vtag"));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.NoExpectedCommit));
        }

        [Test]
        public void Reconcile_MovedTag_ResolvedCommitDiffers_Unknown()
        {
            // SPEC §G8.1 test 3: the tag moved to another commit — the
            // resolved SHA is not the persisted expected one.
            var dep = "file:///fixture/repo.git#" + Sha;
            var moved = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, OtherSha),
                GitPackage("com.fixture.x", dep, commit: OtherSha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => moved);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.CommitMismatch));
        }

        [Test]
        public void Reconcile_ManifestOnlyPartial_Unknown_NeverReAdds()
        {
            // SPEC §G8.1 test 4 / F5 case 3: a manifest entry exists but the
            // package never reached the registered list — partial evidence
            // cannot distinguish applied from unapplied.
            var dep = "file:///fixture/repo.git#" + Sha;
            var partial = View(manifest: Manifest("com.fixture.x", dep));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => partial);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_F5Case4_PreExistingSameSource_Unknown()
        {
            // The snapshot recorded an identical installation before the
            // step ran — the current match cannot be attributed to it.
            var dep = "file:///fixture/repo.git#" + Sha;
            var before = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, before);
            var op = Op(view: () => before);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.PreExisting));
        }

        [Test]
        public void Reconcile_RecordedManifestOnlyTarget_Unknown()
        {
            // The dep was in the manifest at preflight but the package was
            // not registered — a partial pre-existing state whose later
            // resolution cannot be attributed to this step (§F5 case 4).
            var dep = "file:///fixture/repo.git#" + Sha;
            var before = View(manifest: Manifest("com.fixture.x", dep));
            var after = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, before);
            var op = Op(view: () => after);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.PreExisting));
        }

        [Test]
        public void Reconcile_RegistryRecordedDifferentVersion_Unknown()
        {
            // Same-name package at another version existed before the
            // step ran — the current 1.0.0 cannot be attributed to it.
            var id = "com.unity.editorcoroutines@1.0.0";
            var before = View(
                manifest: Manifest("com.unity.editorcoroutines", "0.9.0"),
                lockDeps: Lock("com.unity.editorcoroutines", "0.9.0"),
                RegistryPackage("com.unity.editorcoroutines", "0.9.0"));
            var after = View(
                manifest: Manifest("com.unity.editorcoroutines", "1.0.0"),
                lockDeps: Lock("com.unity.editorcoroutines", "1.0.0"),
                RegistryPackage("com.unity.editorcoroutines", "1.0.0"));
            var snapshot = SnapshotFor(id, before);
            var op = Op(view: () => after);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "a same-name package recorded at preflight is §F5 pre-existing");
        }

        [Test]
        public void Reconcile_GitAdd_PreExistingSameNameRegistryManifest_Unknown()
        {
            // §F5 pre-existing artifact: the manifest already declared the
            // resolved package name under a *registry* version — a source
            // the requested URL+subPath filter can never match — so the
            // fully matching Git install may be that earlier artifact and
            // must not be attributed to this step.
            var dep = "file:///fixture/repo.git#" + Sha;
            var before = View(manifest: Manifest("com.fixture.x", "2.1.0"));
            var after = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, before);
            var op = Op(view: () => after);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null,
                "a same-name manifest entry from another source is §F5 pre-existing");
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.PreExisting));
        }

        [Test]
        public void Reconcile_GitAdd_PreExistingSameNameOtherGitLock_Unknown()
        {
            // The lockfile pinned the same package name to a *different*
            // Git repository before the step ran — outside the requested
            // origin — so a matching install now is not attributable.
            var dep = "file:///fixture/repo.git#" + Sha;
            var otherDep = "https://example.com/other/repo.git#v9";
            var before = View(lockDeps: GitLock("com.fixture.x", otherDep, OtherSha));
            var after = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, before);
            var op = Op(view: () => after);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null,
                "a same-name lock entry from another Git origin is §F5 pre-existing");
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.PreExisting));
        }

        [Test]
        public void Reconcile_GitAdd_PreExistingSameNameRegistryPackage_Unknown()
        {
            // The resolved name was already registered from the registry
            // before the step ran — the Git install that replaced it may
            // be that artifact, so the verdict stays Unknown.
            var dep = "file:///fixture/repo.git#" + Sha;
            var before = View(
                registered: RegistryPackage("com.fixture.x", "2.1.0"));
            var after = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, before);
            var op = Op(view: () => after);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null,
                "a same-name registered package from the registry is §F5 pre-existing");
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.PreExisting));
        }

        [Test]
        public void Reconcile_GitAdd_PreExistingSameNameOtherGitPackage_Unknown()
        {
            // The resolved name was already registered from a *different*
            // Git origin — the requested URL+subPath never matched it, so
            // the matching install now may be that pre-existing package.
            var dep = "file:///fixture/repo.git#" + Sha;
            var otherDep = "https://example.com/other/repo.git#v9";
            var before = View(registered: GitPackage(
                "com.fixture.x", otherDep, commit: OtherSha, requested: "v9"));
            var after = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, before);
            var op = Op(view: () => after);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null,
                "a same-name registered package from another Git origin is §F5 pre-existing");
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.PreExisting));
        }

        [Test]
        public void Reconcile_GitAdd_SnapshotWithoutNameEvidence_Unknown()
        {
            // A snapshot journaled before the name channels existed — or
            // stripped of them — cannot prove a same-name package was
            // absent, so a fully matching install fails closed to
            // Unknown (never Completed on unproven attribution, §F5).
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = (JObject)SnapshotFor(dep, EmptyView()).ToToken();
            snapshot.Remove("names");
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), JsonValue.From(snapshot));

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>(),
                "unproven pre-state name evidence is never Completed");
        }

        [Test]
        public void Reconcile_F5Case5_ModifiedArtifact_Unknown()
        {
            // The snapshot recorded nothing, and a package is installed —
            // but its manifest dep was rewritten to another revision: the
            // artifact was touched by something else (SPEC §F5 case 5).
            var dep = "file:///fixture/repo.git#" + Sha;
            var divergedDep = "file:///fixture/repo.git#other";
            var modified = View(
                manifest: Manifest("com.fixture.x", divergedDep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => modified);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>());
        }

        [Test]
        public void Reconcile_F5Case6_VerdictIsSessionIndependent()
        {
            // F5 case 6 / SPEC §G8.1 test 9: an Editor restart or domain
            // reload carries no in-memory state — the verdict is decided by
            // the persisted snapshot and the persisted package state alone,
            // so judging the same pair through a fresh operation instance
            // answers identically.
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());

            var first = Op(view: () => installed).Reconcile(Context(), snapshot);
            var second = Op(view: () => installed).Reconcile(Context(), snapshot);

            Assert.That(first, Is.InstanceOf<ReconcileResult.Completed>());
            Assert.That(second, Is.InstanceOf<ReconcileResult.Completed>());
        }

        [Test]
        public void Reconcile_RegistryNameVersion_Match_Completed()
        {
            var id = "com.unity.editorcoroutines@1.0.0";
            var installed = View(
                manifest: Manifest("com.unity.editorcoroutines", "1.0.0"),
                lockDeps: Lock("com.unity.editorcoroutines", "1.0.0"),
                RegistryPackage("com.unity.editorcoroutines", "1.0.0"));
            var snapshot = SnapshotFor(id, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Completed>(),
                "registry identity is name plus resolved version (§G8.1)");
        }

        [Test]
        public void Reconcile_RegistryVersionMismatch_Unknown()
        {
            var id = "com.unity.editorcoroutines@1.0.0";
            var installed = View(
                manifest: Manifest("com.unity.editorcoroutines", "2.0.0"),
                lockDeps: Lock("com.unity.editorcoroutines", "2.0.0"),
                RegistryPackage("com.unity.editorcoroutines", "2.0.0"));
            var snapshot = SnapshotFor(id, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.VersionMismatch));
        }

        [Test]
        public void Reconcile_RegistrySameNameDifferentSource_Unknown()
        {
            // A same-named Git-installed package is not the registry add —
            // name alone is never evidence (SPEC §G8.1).
            var id = "com.unity.editorcoroutines@1.0.0";
            var gitDep = "https://github.com/org/e.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.unity.editorcoroutines", gitDep),
                lockDeps: GitLock("com.unity.editorcoroutines", gitDep, Sha),
                GitPackage("com.unity.editorcoroutines", gitDep,
                    commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(id, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.SourceMismatch));
        }

        [Test]
        public void Reconcile_MultipleSameOriginPackages_Unknown()
        {
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha),
                GitPackage("com.fixture.y", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Multiple));
        }

        [Test]
        public void Reconcile_RecordedTargetNowAbsent_Unknown_NotNotApplied()
        {
            // The snapshot recorded the target installed at preflight and
            // it is gone now — the pre-state diverged, so absence cannot be
            // proven consistent (SPEC §F5 rule 5).
            var dep = "file:///fixture/repo.git#" + Sha;
            var before = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, before);
            var op = Op(view: EmptyView);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Diverged));
        }

        [Test]
        public void Reconcile_MalformedSnapshot_Unknown()
        {
            var op = Op(view: EmptyView);

            var verdict = op.Reconcile(Context(), JsonValue.From(new JObject()));

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.SnapshotMalformed));
        }

        [Test]
        public void Reconcile_RequestedRevisionDiverged_Unknown()
        {
            // The resolved package came from the same origin at a different
            // requested revision — the manifest dep text no longer matches
            // the persisted request (SPEC §G8.1 identity).
            var dep = "file:///fixture/repo.git#v1";
            var other = "file:///fixture/repo.git#v2";
            var installed = View(
                manifest: Manifest("com.fixture.x", other),
                lockDeps: GitLock("com.fixture.x", other, OtherSha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: "v1"));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Unknown>());
        }

        // ---------- Reconcile: cross-channel disagreement (§G8.1) ----------

        [Test]
        public void Reconcile_RegistryLockVersionDisagreesWithRegistered_Unknown()
        {
            // The lockfile resolves the name to 2.0.0 while the manifest
            // and the registered package say 1.0.0 — a manifest/lock/
            // Package Manager disagreement is never Completed (§G8.1).
            var id = "com.unity.editorcoroutines@1.0.0";
            var installed = View(
                manifest: Manifest("com.unity.editorcoroutines", "1.0.0"),
                lockDeps: Lock("com.unity.editorcoroutines", "2.0.0"),
                RegistryPackage("com.unity.editorcoroutines", "1.0.0"));
            var snapshot = SnapshotFor(id, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_RegistryManifestSpecDisagreesWithRequest_Unknown()
        {
            // The manifest asks for 2.0.0 while the lockfile and the
            // registered package say 1.0.0 — the same disagreement rule.
            var id = "com.unity.editorcoroutines@1.0.0";
            var installed = View(
                manifest: Manifest("com.unity.editorcoroutines", "2.0.0"),
                lockDeps: Lock("com.unity.editorcoroutines", "1.0.0"),
                RegistryPackage("com.unity.editorcoroutines", "1.0.0"));
            var snapshot = SnapshotFor(id, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_GitLockHashDisagreesWithExpectedCommit_Unknown()
        {
            // The lock entry names the right dependency but its resolved
            // hash is not the persisted expected commit — the installed
            // artifact is not the one the request resolved to.
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, OtherSha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_GitLockWithoutResolvedHash_Unknown()
        {
            // A name-keyed lock entry that carries only the requested URL
            // and no resolved "hash" cannot prove the resolved commit.
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: Lock("com.fixture.x", dep, "git"),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_GitLockRevisionDisagreesWithRequest_Unknown()
        {
            // Same origin and sub-path, but the lockfile pinned a
            // different requested revision — the lock's source spec is
            // not the request's.
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock(
                    "com.fixture.x", "file:///fixture/repo.git#v9", Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_GitLockSubPathDisagreesWithRequest_Unknown()
        {
            // The lock resolves the same repository at a different
            // package sub-path — not the requested source.
            var dep = "file:///fixture/repo.git?path=/pkgs/x#" + Sha;
            var otherSub = "file:///fixture/repo.git?path=/pkgs/y#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", otherSub, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_GitManifestSpecDisagreesWithRequest_Unknown()
        {
            // The manifest names the resolved package at the same
            // origin + sub-path but a different requested revision.
            var dep = "file:///fixture/repo.git#" + Sha;
            var otherRev = "file:///fixture/repo.git#v9";
            var installed = View(
                manifest: Manifest("com.fixture.x", otherRev),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_GitRegisteredSourceDisagrees_Unknown()
        {
            // A package whose dependency text matches the requested Git
            // source but whose Package Manager source is not "git" is
            // not the requested install — the Git mirror of the
            // registry-direction source-mismatch test.
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                new ResolvedPackageView(
                    "com.fixture.x", "1.0.0", "local", dep, Sha, Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(
                unknown!.Reason, Is.EqualTo(PackageAddEvidence.SourceMismatch));
        }

        [Test]
        public void Reconcile_GitSecondManifestEntrySameSource_Unknown()
        {
            // A second manifest key resolving to the requested source is
            // ambiguous — not exactly one agreeing manifest entry.
            var dep = "file:///fixture/repo.git#" + Sha;
            var manifest = Manifest("com.fixture.x", dep);
            manifest["com.fixture.y"] = dep;
            var installed = View(
                manifest: manifest,
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_GitSecondLockEntrySameSource_Unknown()
        {
            // Two lock entries resolving to the requested origin +
            // sub-path under different package names — an ambiguous
            // lockfile, never Completed.
            var dep = "file:///fixture/repo.git#" + Sha;
            var lockDeps = GitLock("com.fixture.x", dep, Sha);
            lockDeps["com.fixture.y"] =
                new JObject { ["version"] = dep, ["hash"] = Sha };
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: lockDeps,
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_RegistryLockSourceEmbedded_Unknown()
        {
            // The lock entry pins the requested name and resolved version
            // but declares "source": "embedded" — the lockfile disagrees
            // with the channel the manifest and the registered package
            // report, so the verdict is Unknown, never Completed
            // (SPEC §G8.1).
            var id = "com.unity.editorcoroutines@1.0.0";
            var installed = View(
                manifest: Manifest("com.unity.editorcoroutines", "1.0.0"),
                lockDeps: Lock(
                    "com.unity.editorcoroutines", "1.0.0", "embedded"),
                RegistryPackage("com.unity.editorcoroutines", "1.0.0"));
            var snapshot = SnapshotFor(id, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_RegistryLockSourceMissing_Unknown()
        {
            // A lock entry with no "source" field at all declares
            // nothing about the channel the package resolved through —
            // the same disagreement rule, never Completed.
            var id = "com.unity.editorcoroutines@1.0.0";
            var lockDeps = new JObject
            {
                ["com.unity.editorcoroutines"] = new JObject
                {
                    ["version"] = "1.0.0",
                },
            };
            var installed = View(
                manifest: Manifest("com.unity.editorcoroutines", "1.0.0"),
                lockDeps: lockDeps,
                RegistryPackage("com.unity.editorcoroutines", "1.0.0"));
            var snapshot = SnapshotFor(id, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_GitLockSourceRegistry_Unknown()
        {
            // A Git request whose lock entry carries the matching
            // dependency text and resolved hash but declares
            // "source": "registry" — the channels disagree on how the
            // package was installed, never Completed.
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha, "registry"),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_GitLockSourceNonString_Unknown()
        {
            // A non-string "source" is no evidence of the install
            // channel at all — the lock entry can never prove the Git
            // source the request requires.
            var dep = "file:///fixture/repo.git#" + Sha;
            var lockDeps = new JObject
            {
                ["com.fixture.x"] = new JObject
                {
                    ["version"] = dep,
                    ["hash"] = Sha,
                    ["source"] = 7,
                },
            };
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: lockDeps,
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(unknown!.Reason, Is.EqualTo(PackageAddEvidence.Partial));
        }

        [Test]
        public void Reconcile_LockSourceMatchesRequestKind_Completed()
        {
            // Positive control for the lock "source" check: a lock entry
            // whose declared source equals the request's kind keeps the
            // full §G8.1 evidence row Completed.
            var id = "com.unity.editorcoroutines@1.0.0";
            var installed = View(
                manifest: Manifest("com.unity.editorcoroutines", "1.0.0"),
                lockDeps: Lock("com.unity.editorcoroutines", "1.0.0"),
                RegistryPackage("com.unity.editorcoroutines", "1.0.0"));
            var snapshot = SnapshotFor(id, EmptyView());
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            Assert.That(verdict, Is.InstanceOf<ReconcileResult.Completed>(),
                "a lock source matching the request's kind stays Completed");
        }

        // ---------- Reconcile: unavailable evidence channels (§G8.1) ----------

        [Test]
        public void Reconcile_UnreadableManifest_Unknown_NeverNotApplied()
        {
            // An unreadable manifest is not proof of absence: §G8.1's
            // NotApplied requires absence proven in the manifest, the
            // lockfile and the registered list alike.
            var dep = "file:///fixture/repo.git#" + Sha;
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => UnreadableView(manifestRead: false));

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(
                unknown!.Reason, Is.EqualTo(PackageAddEvidence.EvidenceUnreadable));
        }

        [Test]
        public void Reconcile_MalformedManifest_Unknown_NeverNotApplied()
        {
            // A manifest.json that fails to parse yields no manifest
            // evidence — the verdict is Unknown through the real
            // file-read path, never NotApplied.
            var dep = "file:///fixture/repo.git#" + Sha;
            var snapshot = SnapshotFor(dep, EmptyView());
            var dir = TempPackagesDir();
            try
            {
                File.WriteAllText(
                    Path.Combine(dir, "manifest.json"), "{ not json");
                var view = new PackageInstallView(
                    PackageInstallView.ReadDependencies(
                        dir, "manifest.json", out var manifestRead),
                    null,
                    Array.Empty<ResolvedPackageView>(),
                    manifestRead: manifestRead);
                var op = Op(view: () => view);

                var verdict = op.Reconcile(Context(), snapshot);

                var unknown = verdict as ReconcileResult.Unknown;
                Assert.That(unknown, Is.Not.Null);
                Assert.That(
                    unknown!.Reason,
                    Is.EqualTo(PackageAddEvidence.EvidenceUnreadable));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Reconcile_UnreadableLockfile_Unknown_NeverNotApplied()
        {
            var dep = "file:///fixture/repo.git#" + Sha;
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => UnreadableView(lockRead: false));

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(
                unknown!.Reason, Is.EqualTo(PackageAddEvidence.EvidenceUnreadable));
        }

        [Test]
        public void Reconcile_MalformedLockfile_Unknown_NeverNotApplied()
        {
            var dep = "file:///fixture/repo.git#" + Sha;
            var snapshot = SnapshotFor(dep, EmptyView());
            var dir = TempPackagesDir();
            try
            {
                File.WriteAllText(
                    Path.Combine(dir, "packages-lock.json"), "{ not json");
                var view = new PackageInstallView(
                    null,
                    PackageInstallView.ReadDependencies(
                        dir, "packages-lock.json", out var lockRead),
                    Array.Empty<ResolvedPackageView>(),
                    lockRead: lockRead);
                var op = Op(view: () => view);

                var verdict = op.Reconcile(Context(), snapshot);

                var unknown = verdict as ReconcileResult.Unknown;
                Assert.That(unknown, Is.Not.Null);
                Assert.That(
                    unknown!.Reason,
                    Is.EqualTo(PackageAddEvidence.EvidenceUnreadable));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Reconcile_UnreadableRegisteredList_Unknown_NeverNotApplied()
        {
            var dep = "file:///fixture/repo.git#" + Sha;
            var snapshot = SnapshotFor(dep, EmptyView());
            var op = Op(view: () => UnreadableView(registeredRead: false));

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(
                unknown!.Reason, Is.EqualTo(PackageAddEvidence.EvidenceUnreadable));
        }

        [Test]
        public void Reconcile_RecordedAbsenceFromUnreadChannel_Unknown()
        {
            // The preflight capture itself could not read a channel, so
            // the persisted snapshot's empty pre-state is an unproven
            // absence — even a fully read empty state now must not
            // reconcile NotApplied on it.
            var dep = "file:///fixture/repo.git#" + Sha;
            var snapshot = SnapshotFor(dep, UnreadableView(lockRead: false));
            var op = Op(view: EmptyView);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(
                unknown!.Reason,
                Is.EqualTo(PackageAddEvidence.RecordedStateIncomplete));
        }

        [Test]
        public void Reconcile_RecordedUnreadManifest_MatchingInstall_Unknown()
        {
            // The preflight manifest channel could not be read, so the
            // snapshot's empty manifest record cannot prove the target
            // was absent — a fully matching installation now may be
            // pre-existing and must not be attributed to this step
            // (SPEC §F5).
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, UnreadableView(manifestRead: false));
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(
                unknown!.Reason,
                Is.EqualTo(PackageAddEvidence.RecordedStateIncomplete));
        }

        [Test]
        public void Reconcile_RecordedUnreadLock_MatchingInstall_Unknown()
        {
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, UnreadableView(lockRead: false));
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(
                unknown!.Reason,
                Is.EqualTo(PackageAddEvidence.RecordedStateIncomplete));
        }

        [Test]
        public void Reconcile_RecordedUnreadRegistered_MatchingInstall_Unknown()
        {
            var dep = "file:///fixture/repo.git#" + Sha;
            var installed = View(
                manifest: Manifest("com.fixture.x", dep),
                lockDeps: GitLock("com.fixture.x", dep, Sha),
                GitPackage("com.fixture.x", dep, commit: Sha, requested: Sha));
            var snapshot = SnapshotFor(dep, UnreadableView(registeredRead: false));
            var op = Op(view: () => installed);

            var verdict = op.Reconcile(Context(), snapshot);

            var unknown = verdict as ReconcileResult.Unknown;
            Assert.That(unknown, Is.Not.Null);
            Assert.That(
                unknown!.Reason,
                Is.EqualTo(PackageAddEvidence.RecordedStateIncomplete));
        }

        // ---------- ReadDependencies channel reads ----------

        [TestCase("manifest.json")]
        [TestCase("packages-lock.json")]
        public void ReadDependencies_MissingFile_IsNotRead(string fileName)
        {
            var dir = TempPackagesDir();
            try
            {
                var deps = PackageInstallView.ReadDependencies(
                    dir, fileName, out var read);

                Assert.That(read, Is.False);
                Assert.That(deps, Is.Null);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [TestCase("manifest.json")]
        [TestCase("packages-lock.json")]
        public void ReadDependencies_MalformedJson_IsNotRead(string fileName)
        {
            var dir = TempPackagesDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, fileName), "{ not json");

                var deps = PackageInstallView.ReadDependencies(
                    dir, fileName, out var read);

                Assert.That(read, Is.False);
                Assert.That(deps, Is.Null);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void ReadDependencies_NonObjectDependencies_IsNotRead()
        {
            // JSON that parses but whose "dependencies" is not an object
            // is malformed evidence — not read, never "zero deps".
            var dir = TempPackagesDir();
            try
            {
                File.WriteAllText(
                    Path.Combine(dir, "manifest.json"),
                    "{\"dependencies\": 42}");

                var deps = PackageInstallView.ReadDependencies(
                    dir, "manifest.json", out var read);

                Assert.That(read, Is.False);
                Assert.That(deps, Is.Null);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void ReadDependencies_LockedFile_IsNotRead()
        {
            // A share-locked file fails the read — the exception path
            // also reports "not read", never a silent empty channel.
            var dir = TempPackagesDir();
            try
            {
                var path = Path.Combine(dir, "manifest.json");
                File.WriteAllText(path, "{\"dependencies\":{}}");
                using (new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var deps = PackageInstallView.ReadDependencies(
                        dir, "manifest.json", out var read);

                    Assert.That(read, Is.False);
                    Assert.That(deps, Is.Null);
                }
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void ReadDependencies_ValidFile_ReadsDependenciesObject()
        {
            var dir = TempPackagesDir();
            try
            {
                File.WriteAllText(
                    Path.Combine(dir, "manifest.json"),
                    "{\"dependencies\":{\"com.example.pkg\":\"1.0.0\"}}");

                var deps = PackageInstallView.ReadDependencies(
                    dir, "manifest.json", out var read);

                Assert.That(read, Is.True);
                Assert.That(deps, Is.Not.Null);
                Assert.That(
                    deps!["com.example.pkg"]!.ToString(), Is.EqualTo("1.0.0"));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ---------- Cancel ----------

        [Test]
        public void Cancel_EndsTheWaitWithoutThrowing()
        {
            var token = PackageResolvedCondition.InitialToken(RegistryId, true);
            var op = Op();

            Assert.DoesNotThrow(() => op.Cancel(Context(resumeToken: token)));
            Assert.DoesNotThrow(() => op.Cancel(Context()),
                "Cancel on a non-Waiting step is a no-op");
        }
    }
}
