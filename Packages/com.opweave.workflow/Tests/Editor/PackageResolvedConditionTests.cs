#nullable enable

// T-W4.1-02 [Edit] tests: the SPEC §G4 PackageResolved(id) row — the
// wait's whole state restores from the resume token alone; while the
// request's domain still holds the tracked Request its IsCompleted and
// Status judge (a Failure is OW-UNITY-009 with the request's error text);
// once the slot no longer resolves — the post-reload case — the
// registered package list judges on idle checks only (SPEC §T.4 L-07):
// an add is Done when the target is listed, a remove when the name is
// gone.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEditor.PackageManager;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// SPEC §G4: <c>PackageResolved(id)</c> — same-domain Request
    /// IsCompleted/Status, post-reload registered-package-list membership
    /// gated on an idle Editor, OW-UNITY-009 on request failure.
    /// </summary>
    internal sealed class PackageResolvedConditionTests
    {
        private static readonly IReadOnlyList<PackageResolvedCondition.RegisteredPackage>
            NoPackages =
                Array.Empty<PackageResolvedCondition.RegisteredPackage>();

        private static PackageResolvedCondition Condition(
            Func<int, PackageResolvedCondition.RequestSnapshot?>? requestSnapshot = null,
            Action<int>? releaseRequest = null,
            Func<bool>? isCompiling = null,
            Func<bool>? isUpdating = null,
            Func<IReadOnlyList<PackageResolvedCondition.RegisteredPackage>>? registered = null) =>
            new PackageResolvedCondition(
                requestSnapshot ?? (static _ => null),
                releaseRequest ?? (static _ => { }),
                isCompiling ?? (static () => false),
                isUpdating ?? (static () => false),
                registered ?? (static () => NoPackages));

        private static JsonValue Token(
            string id = "com.example.pkg", bool expectPresent = true,
            int? request = null, string? name = "com.example.pkg",
            string? domain = null) =>
            PackageResolvedCondition.InitialToken(
                id, expectPresent, request, name, domain);

        private static PackageResolvedCondition.RegisteredPackage Entry(
            string name, string? dependency = null) =>
            new PackageResolvedCondition.RegisteredPackage(name, dependency);

        private static PackageResolvedCondition.RequestSnapshot InFlight() =>
            new PackageResolvedCondition.RequestSnapshot(
                isCompleted: false, StatusCode.InProgress, null);

        private static PackageResolvedCondition.RequestSnapshot Succeeded() =>
            new PackageResolvedCondition.RequestSnapshot(
                isCompleted: true, StatusCode.Success, null);

        private static PackageResolvedCondition.RequestSnapshot Failed(string? message) =>
            new PackageResolvedCondition.RequestSnapshot(
                isCompleted: true, StatusCode.Failure, message);

        // ---------- same-domain: the Request's IsCompleted and Status ----------

        [Test]
        public void Check_RequestInFlight_StaysPending()
        {
            var verdict = Condition(requestSnapshot: _ => InFlight())
                .Check(Token(request: 3));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_RequestSucceeded_ReturnsDone()
        {
            var verdict = Condition(requestSnapshot: _ => Succeeded())
                .Check(Token(request: 3));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void Check_RequestFailed_ReturnsOwUnity009WithError()
        {
            var verdict = Condition(
                    requestSnapshot: _ => Failed(
                        "com.example.missing was not found in the registry"))
                .Check(Token(request: 3));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Failed));
            var error = verdict.Error!;
            Assert.That(error.Code, Is.EqualTo("OW-UNITY-009"));
            Assert.That(error.Hint, Does.Contain("com.example.missing"));
        }

        [Test]
        public void Check_RequestFailedWithoutMessage_StillFails()
        {
            var verdict = Condition(requestSnapshot: _ => Failed(null))
                .Check(Token(request: 3));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Failed));
            Assert.That(verdict.Error!.Code, Is.EqualTo("OW-UNITY-009"));
            Assert.That(verdict.Error!.Hint, Does.Contain("Package Manager"));
        }

        [Test]
        public void Check_RequestCompletedWhileInProgress_StaysPending()
        {
            // A completed flag under a still-InProgress status is mid-
            // transition state — never a verdict.
            var verdict = Condition(
                    requestSnapshot: _ => new PackageResolvedCondition.RequestSnapshot(
                        isCompleted: true, StatusCode.InProgress, null))
                .Check(Token(request: 3));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_TerminalVerdicts_ReleaseTheSlot()
        {
            var released = new List<int>();
            var done = Condition(
                    requestSnapshot: _ => Succeeded(),
                    releaseRequest: released.Add)
                .Check(Token(request: 7));
            Assert.That(done.Kind, Is.EqualTo(WaitStatusKind.Done));
            var failed = Condition(
                    requestSnapshot: _ => Failed("nope"),
                    releaseRequest: released.Add)
                .Check(Token(request: 9));
            Assert.That(failed.Kind, Is.EqualTo(WaitStatusKind.Failed));
            Assert.That(released, Is.EqualTo(new[] { 7, 9 }));
        }

        [Test]
        public void Check_RequestVerdictIgnoresBusyFlags()
        {
            // The §G4 request column needs no idle Editor: a completed
            // request is judged even while compiling/updating.
            var verdict = Condition(
                    requestSnapshot: _ => Succeeded(),
                    isCompiling: () => true,
                    isUpdating: () => true)
                .Check(Token(request: 3));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        // ---------- post-reload / requestless: the registered package list ----------

        [Test]
        public void Check_AddedNameListed_ReturnsDone()
        {
            var verdict = Condition(
                    registered: () => new[] { Entry("com.example.pkg") })
                .Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void Check_AddedNameMissing_StaysPending()
        {
            var verdict = Condition().Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_RemovedNameGone_ReturnsDone()
        {
            var verdict = Condition().Check(
                Token(expectPresent: false, name: "com.example.pkg"));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void Check_RemovedNameStillListed_StaysPending()
        {
            var verdict = Condition(
                    registered: () => new[] { Entry("com.example.pkg") })
                .Check(Token(expectPresent: false, name: "com.example.pkg"));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_Compiling_StaysPendingEvenWhenListed()
        {
            // The §G4 idle gate: membership is only read once the Editor
            // is neither compiling nor updating.
            var verdict = Condition(
                    isCompiling: () => true,
                    registered: () => new[] { Entry("com.example.pkg") })
                .Check(Token());
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_Updating_StaysPendingEvenWhenGone()
        {
            var verdict = Condition(
                    isUpdating: () => true)
                .Check(Token(expectPresent: false, name: "com.example.pkg"));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_DeadRequestSlot_FallsBackToList()
        {
            // A slot that resolves to nothing is the reload having dropped
            // the Request — the list judgement takes over.
            var verdict = Condition(
                    requestSnapshot: _ => null,
                    registered: () => new[] { Entry("com.example.pkg") })
                .Check(Token(request: 5));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        // ---------- the domain stamp keeps foreign slots out ----------

        [Test]
        public void Check_ForeignDomainSlot_NeverReadsTheRequestTable()
        {
            // Codex F1: after the reload another wait took the old token's
            // slot — a resumed check must never read that unrelated
            // Request's verdict. The domain stamp makes the slot foreign,
            // so the registered-package judgement applies instead.
            var snapshots = 0;
            var releases = new List<int>();
            var verdict = Condition(
                    requestSnapshot: _ =>
                    {
                        snapshots++;
                        return Failed("unrelated request");
                    },
                    releaseRequest: releases.Add,
                    registered: () => new[] { Entry("com.example.pkg") })
                .Check(Token(request: 3, domain: "old-domain"));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
            Assert.That(snapshots, Is.EqualTo(0),
                "a foreign-domain token consulted this domain's request table");
            Assert.That(releases, Is.Empty,
                "a foreign-domain token released this domain's request slot");
        }

        [Test]
        public void Check_ForeignDomainSlot_StaysPendingOnTheListJudgement()
        {
            // The same slot collision with the target still absent: a
            // Request the token does not own can never complete the wait —
            // the §G4 post-reload column keeps it Pending.
            var verdict = Condition(
                    requestSnapshot: _ => Succeeded())
                .Check(Token(request: 3, domain: "old-domain"));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_DomainlessSlot_TakesTheRegisteredPackagePath()
        {
            // A token that names a slot without a domain stamp cannot
            // prove the Request is this domain's — it is judged like a
            // resumed wait, by the registered package list alone.
            var obj = (JObject)Token(request: 3).ToToken();
            obj.Remove("domain");
            var snapshots = 0;
            var verdict = Condition(
                    requestSnapshot: _ =>
                    {
                        snapshots++;
                        return Succeeded();
                    })
                .Check(JsonValue.From(obj));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
            Assert.That(snapshots, Is.EqualTo(0));
        }

        [Test]
        public void Check_CurrentDomainSlot_StillTakesTheRequestPath()
        {
            // The stamp preserves the §G4 same-domain column: a token
            // minted in this domain is judged by its Request alone.
            var verdict = Condition(
                    requestSnapshot: _ => Succeeded(),
                    registered: () => NoPackages)
                .Check(Token(request: 3));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        // ---------- Git ids: the nameless add ----------

        private const string GitId = "https://github.com/example/pkg.git?path=/sub#v1";

        [Test]
        public void Check_GitAdd_DependencyMatch_ReturnsDone()
        {
            // The resolved package registers under a learned name; the
            // request's Git origin stays verbatim in its packageId — that
            // is "対象" in the registered list (SPEC §T.4 L-07), even when
            // the resolved name was already there (an idempotent re-add).
            var verdict = Condition(
                    registered: () => new[]
                    {
                        Entry("com.resolved.name", GitId),
                        Entry("com.other", "com.other@1.0.0"),
                    })
                .Check(Token(id: GitId, name: null));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void Check_GitAdd_UnrelatedNewPackage_StaysPending()
        {
            // Codex F2: an unrelated package appearing during resolution
            // is not "対象" — with no registered entry tied to the
            // requested Git origin the wait stays Pending and the step's
            // deadline governs.
            var verdict = Condition(
                    registered: () => new[]
                    {
                        Entry("com.unrelated.new", "com.unrelated.new@1.0.0"),
                        Entry("com.other", "com.other@1.0.0"),
                    })
                .Check(Token(id: GitId, name: null));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_GitAdd_NothingRegistered_StaysPending()
        {
            var verdict = Condition(
                    registered: () => new[] { Entry("com.other", null) })
                .Check(Token(id: GitId, name: null));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_GitAdd_UnresolvedDependency_StaysPending()
        {
            // A registered entry whose dependency is another Git origin is
            // not the target — the SG-19 origin comparison, not text match.
            var verdict = Condition(
                    registered: () => new[]
                    {
                        Entry("com.resolved.name",
                            "https://github.com/other/repo.git?path=/sub#v1"),
                    })
                .Check(Token(id: GitId, name: null));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_GitAdd_NonGitDependency_StaysPending()
        {
            // A registered name whose dependency text is not the requested
            // Git origin is never the target — the Git-origin match alone
            // identifies "対象" for a nameless add.
            var verdict = Condition(
                    registered: () => new[]
                    {
                        Entry("com.resolved.name", "unrelated-dep"),
                    })
                .Check(Token(id: GitId, name: null));
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        // ---------- the token is the whole state ----------

        [Test]
        public void Check_TokenIsTheWholeState_FreshConditionContinues()
        {
            // Two condition instances handed the same token judge it
            // identically — the §G4 "条件は resumeToken だけから復元できる"
            // rule.
            var token = Token(request: 4);
            var first = Condition(requestSnapshot: _ => InFlight()).Check(token);
            var second = Condition(requestSnapshot: _ => InFlight()).Check(token);
            Assert.That(first.Kind, Is.EqualTo(second.Kind));
            Assert.That(first.Kind, Is.EqualTo(WaitStatusKind.Pending));
        }

        [Test]
        public void Check_PendingHandsBackTheToken()
        {
            var token = Token(request: 4);
            var verdict = Condition(requestSnapshot: _ => InFlight()).Check(token);
            Assert.That(verdict.ResumeToken!.ToToken().ToString(),
                Is.EqualTo(token.ToToken().ToString()));
        }

        // ---------- malformed tokens ----------

        [Test]
        public void Check_NullToken_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => Condition().Check(null!));
        }

        [Test]
        public void Check_NonObjectToken_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => Condition().Check(JsonValue.From(new JArray(1, 2))));
        }

        [Test]
        public void Check_TokenWithoutId_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => Condition().Check(
                    JsonValue.From(new JObject { ["expectPresent"] = true })));
        }

        [Test]
        public void Check_TokenWithEmptyId_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => Condition().Check(
                    JsonValue.From(new JObject
                    {
                        ["id"] = "", ["expectPresent"] = true,
                    })));
        }

        [Test]
        public void Check_TokenWithoutExpect_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => Condition().Check(
                    JsonValue.From(new JObject { ["id"] = "com.example.pkg" })));
        }

        [Test]
        public void Check_RemoveTokenWithoutName_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => Condition().Check(
                    JsonValue.From(new JObject
                    {
                        ["id"] = "com.example.pkg", ["expectPresent"] = false,
                    })));
        }

        // ---------- InitialToken ----------

        [Test]
        public void InitialToken_WritesAllFields()
        {
            var token = Token(
                id: "com.example.pkg@1.2.3", expectPresent: true, request: 4,
                name: "com.example.pkg");
            var obj = (JObject)token.ToToken();
            Assert.That((string)obj["id"]!, Is.EqualTo("com.example.pkg@1.2.3"));
            Assert.That((bool)obj["expectPresent"]!, Is.True);
            Assert.That((int)obj["request"]!, Is.EqualTo(4));
            Assert.That((string)obj["domain"]!, Is.Not.Empty);
            Assert.That((string)obj["name"]!, Is.EqualTo("com.example.pkg"));
        }

        [Test]
        public void InitialToken_OmitsAbsentFields()
        {
            var obj = (JObject)Token(request: null, name: null).ToToken();
            Assert.That(obj["request"], Is.Null);
            Assert.That(obj["domain"], Is.Null);
            Assert.That(obj["name"], Is.Null);
            Assert.That(obj["baseline"], Is.Null);
        }

        [Test]
        public void InitialToken_PublicRequestless_ParsesRegistryName()
        {
            var obj = (JObject)PackageResolvedCondition
                .InitialToken("com.example.pkg@1.2.3", expectPresent: true)
                .ToToken();
            Assert.That((string)obj["name"]!, Is.EqualTo("com.example.pkg"));
            Assert.That(obj["request"], Is.Null);
            Assert.That(obj["domain"], Is.Null);
        }

        [Test]
        public void InitialToken_PublicRequestless_GitAdd_WritesNoNameOrSlot()
        {
            // The nameless (Git) add: no registry name is known yet, and a
            // requestless token carries neither slot nor domain stamp —
            // the registered-list Git-origin match identifies the target.
            var obj = (JObject)PackageResolvedCondition
                .InitialToken("https://github.com/example/pkg.git", expectPresent: true)
                .ToToken();
            Assert.That(obj["name"], Is.Null);
            Assert.That(obj["request"], Is.Null);
            Assert.That(obj["domain"], Is.Null);
            Assert.That(obj["baseline"], Is.Null);
        }

        [Test]
        public void InitialToken_PublicRemoveWithoutName_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => PackageResolvedCondition.InitialToken(
                    "https://github.com/example/pkg.git", expectPresent: false));
        }

        [Test]
        public void InitialToken_EmptyId_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => PackageResolvedCondition.InitialToken("", expectPresent: true));
        }

        [Test]
        public void NameOf_ReturnsStoredName()
        {
            Assert.That(
                PackageResolvedCondition.NameOf(Token(name: "com.example.pkg")),
                Is.EqualTo("com.example.pkg"));
            Assert.That(
                PackageResolvedCondition.NameOf(Token(name: null)),
                Is.Null);
        }
    }
}
