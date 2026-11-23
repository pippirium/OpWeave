#nullable enable

// T-W2.7-02 [Edit] tests: local:T-W2.7-02:asset-import-wait for the SPEC §G4
// AssetImported condition itself — Done when the Editor is not updating and
// every path loads through LoadMainAssetAtPath, Failed with OW-UNITY-008
// after three consecutive null loads while not updating, and never a verdict
// while the Editor is updating. Every check runs through the seam
// constructor, so the streak, the isUpdating gate and the import-error
// attachment are proven deterministically; the resume-token-only restore —
// the §G4 contract — is proven by handing a Pending token to a fresh
// condition instance.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// SPEC §G4 <c>AssetImported(paths)</c>: the completion and failure
    /// judgements of the asset-import wait condition.
    /// </summary>
    internal sealed class AssetImportedConditionTests
    {
        private const string PathA = "Assets/Probe/a.txt";
        private const string PathB = "Assets/Probe/b.txt";

        private static readonly IReadOnlyList<string> NoErrors =
            Array.Empty<string>();

        private static UnityEngine.Object Loaded() =>
            ScriptableObject.CreateInstance<ProbeAsset>();

        private static AssetImportedCondition Condition(
            Func<bool>? isUpdating = null,
            Func<string, UnityEngine.Object?>? load = null,
            Func<IReadOnlyList<string>>? errors = null) =>
            new AssetImportedCondition(
                isUpdating ?? (() => false),
                load ?? (_ => Loaded()),
                errors ?? (() => NoErrors));

        private static JsonValue Token(
            string path = PathA, int nulls = 0, params string[] errors)
        {
            var parsed = JsonValue.Parse(
                "{\"paths\":[\"" + path + "\"],\"nulls\":" + nulls +
                ",\"errors\":[" + string.Join(
                    ",", Array.ConvertAll(errors, e => "\"" + e + "\"")) + "]}");
            Assert.That(parsed.Ok, Is.True, "the fixture token must parse");
            return parsed.Value!;
        }

        // SPEC §G4 failure column: while the Editor is updating a null load
        // is the import still running, so no check is judged at all — the
        // token's streak is handed back unchanged.
        [Test]
        public void Check_WhileUpdating_StaysPendingAndDoesNotCountNulls()
        {
            var condition = Condition(isUpdating: () => true, load: _ => null);

            var first = condition.Check(Token());
            var second = condition.Check(first.ResumeToken!);
            var third = condition.Check(second.ResumeToken!);

            foreach (var status in new[] { first, second, third })
            {
                Assert.That(status.Kind, Is.EqualTo(WaitStatusKind.Pending));
                var token = status.ResumeToken!.ToToken();
                Assert.That((int?)token["nulls"], Is.EqualTo(0),
                    "an updating check is never a null-load check");
            }
        }

        // SPEC §G4 completion column: !isUpdating and every path loads.
        [Test]
        public void Check_AllPathsLoad_IsDone()
        {
            var condition = Condition();
            var token = AssetImportedCondition.InitialToken(new[] { PathA, PathB });

            var status = condition.Check(token);

            Assert.That(status.Kind, Is.EqualTo(WaitStatusKind.Done));
        }

        [Test]
        public void Check_OnePathStillMissing_StaysPending()
        {
            var loaded = new HashSet<string>(StringComparer.Ordinal) { PathA };
            var condition = Condition(load: path => loaded.Contains(path) ? Loaded() : null);
            var token = AssetImportedCondition.InitialToken(new[] { PathA, PathB });

            var status = condition.Check(token);

            Assert.That(status.Kind, Is.EqualTo(WaitStatusKind.Pending));
            Assert.That((int?)status.ResumeToken!.ToToken()["nulls"], Is.EqualTo(1));
        }

        // SPEC §G4 failure column: !isUpdating and null three checks in a
        // row → OW-UNITY-008 with the step's import errors attached.
        [Test]
        public void Check_ThreeConsecutiveNulls_FailsWithOwUnity008()
        {
            var condition = Condition(load: _ => null);

            var first = condition.Check(Token());
            var second = condition.Check(first.ResumeToken!);
            var third = condition.Check(second.ResumeToken!);

            Assert.That(first.Kind, Is.EqualTo(WaitStatusKind.Pending));
            Assert.That(second.Kind, Is.EqualTo(WaitStatusKind.Pending));
            Assert.That((int?)second.ResumeToken!.ToToken()["nulls"], Is.EqualTo(2));
            Assert.That(third.Kind, Is.EqualTo(WaitStatusKind.Failed));
            Assert.That(third.Error!.Code, Is.EqualTo(ErrorCodes.OW_UNITY_008));
            Assert.That(third.Error.Severity, Is.EqualTo(Severity.Error));
        }

        // An updating check between null loads is not a loaded check — it
        // neither counts nor breaks the consecutive streak.
        [Test]
        public void Check_UpdatingBetweenNulls_KeepsTheStreakAlive()
        {
            var updating = false;
            var condition = Condition(isUpdating: () => updating, load: _ => null);

            var first = condition.Check(Token());
            updating = true;
            var held = condition.Check(first.ResumeToken!);
            updating = false;
            var second = condition.Check(held.ResumeToken!);
            var third = condition.Check(second.ResumeToken!);

            Assert.That(held.Kind, Is.EqualTo(WaitStatusKind.Pending));
            Assert.That((int?)held.ResumeToken!.ToToken()["nulls"], Is.EqualTo(1),
                "the updating check left the streak untouched");
            Assert.That(second.Kind, Is.EqualTo(WaitStatusKind.Pending));
            Assert.That((int?)second.ResumeToken!.ToToken()["nulls"], Is.EqualTo(2),
                "the streak resumes counting where the updating check left it");
            Assert.That(third.Kind, Is.EqualTo(WaitStatusKind.Failed));
        }

        // SPEC §G4 "条件は resumeToken だけから復元できる": a condition
        // built in a fresh domain — a brand-new instance — continues the
        // streak the token carries; nothing else needs to survive.
        [Test]
        public void Check_FreshInstanceContinuesTheStreakFromTheTokenAlone()
        {
            var first = Condition(load: _ => null).Check(Token());
            var second = Condition(load: _ => null).Check(first.ResumeToken!);

            var restored = Condition(load: _ => null);
            var third = restored.Check(second.ResumeToken!);

            Assert.That(third.Kind, Is.EqualTo(WaitStatusKind.Failed),
                "a fresh condition must reach the verdict the lost instance had earned");
            Assert.That(third.Error!.Code, Is.EqualTo(ErrorCodes.OW_UNITY_008));
        }

        // SPEC §G4 "ステップ中のインポートエラーを添付": error entries that
        // name a waited path are folded into the token and attached to the
        // OW-UNITY-008 hint; unrelated entries never reach it.
        [Test]
        public void Check_AttachesCapturedImportErrorsToTheFailure()
        {
            var reported = new[]
            {
                "Asset import failed for '" + PathA + "' - bad bytes",
                "An unrelated error about something else",
            };
            var condition = Condition(load: _ => null, errors: () => reported);

            var token = Token();
            for (var i = 0; i < AssetImportedCondition.MaxConsecutiveNulls - 1; i++)
            {
                var status = condition.Check(token);
                Assert.That(status.Kind, Is.EqualTo(WaitStatusKind.Pending));
                token = status.ResumeToken!;
            }

            var pending = token.ToToken();
            var folded = (JArray)pending["errors"]!;
            Assert.That(folded, Has.Count.EqualTo(1));
            Assert.That((string?)folded[0], Is.EqualTo(reported[0]),
                "only the error naming the waited path is folded into the token");

            var verdict = condition.Check(token);
            Assert.That(verdict.Kind, Is.EqualTo(WaitStatusKind.Failed));
            Assert.That(verdict.Error!.Hint, Does.Contain("bad bytes"));
            Assert.That(verdict.Error.Hint, Does.Not.Contain("unrelated"));
        }

        // The fold is monotone: a second check of the same token adds
        // nothing twice and drops nothing already captured.
        [Test]
        public void Check_DoesNotDuplicateCapturedErrors()
        {
            var reported = new[] { "import error at " + PathA };
            var condition = Condition(load: _ => null, errors: () => reported);

            var once = condition.Check(Token());
            var twice = condition.Check(once.ResumeToken!);

            var folded = (JArray)twice.ResumeToken!.ToToken()["errors"]!;
            Assert.That(folded, Has.Count.EqualTo(1));
        }

        // A token this condition never wrote is refused: guessing at a
        // malformed wait state would misjudge the step.
        [Test]
        public void Check_MalformedToken_IsRefused()
        {
            var parsed = JsonValue.Parse("{\"notPaths\":[]}");
            Assert.That(parsed.Ok, Is.True);
            var condition = Condition(load: _ => null);

            Assert.Throws<ArgumentException>(() => condition.Check(parsed.Value!));
        }

        // The fold already runs at Waiting time — the queued import can
        // report its failure before the first Check, and only the token
        // survives a reload — so the same path filter applies there.
        [Test]
        public void InitialToken_FoldsOnlyErrorsNamingAWaitedPath()
        {
            var reported = new[]
            {
                "Asset import failed for '" + PathA + "' - bad bytes",
                "An unrelated error about something else",
            };

            var token = AssetImportedCondition.InitialToken(new[] { PathA }, reported);
            var folded = (JArray)token.ToToken()["errors"]!;

            Assert.That(folded, Has.Count.EqualTo(1));
            Assert.That((string?)folded[0], Is.EqualTo(reported[0]));
        }

        [Test]
        public void InitialToken_CarriesPathsAndAnEmptyStreak()
        {
            var token = AssetImportedCondition.InitialToken(new[] { PathA, PathB });
            var obj = token.ToToken();

            Assert.That(obj["paths"]!.ToObject<string[]>(),
                Is.EqualTo(new[] { PathA, PathB }));
            Assert.That((int?)obj["nulls"], Is.EqualTo(0));
            Assert.That((JArray)obj["errors"]!, Is.Empty);
            Assert.That(AssetImportedCondition.PathsOf(token),
                Is.EqualTo(new[] { PathA, PathB }));
        }
    }
}
