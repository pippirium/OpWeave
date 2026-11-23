#nullable enable
// T-W2.4-02 [Edit] tests: local:T-W2.4-02:changeset — the identity rule of the
// SPEC §F7 change list. Two reports are one element when they describe one
// target of one step, and two elements when their durable §F5 identifiers say
// they touched different artifacts, even when the path is the same. The dedup
// runs on every writer's output — the Operation's ctx.Changes record, the import
// the AssetPostprocessor reports for the same change, and the elements a
// reconstruction reads back out of the journal — so it is pinned here directly.
using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class ChangeSetIdentityTests
    {
        private const string ScenePath = "ProbePair";
        private const string FirstGlobalId = "GO:0123456789abcdef0123456789abcdef:1001";
        private const string SecondGlobalId = "GO:0123456789abcdef0123456789abcdef:1002";
        private const string FirstGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string SecondGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static RunChangeSet NewSet() =>
            new RunChangeSet(
                "run_01J000000000000000000000TEST",
                new HashSet<string>(StringComparer.Ordinal));

        private static ChangeRecord Record(
            int step,
            string nodeId,
            ChangeAction action,
            string kind,
            string path,
            string? guid = null,
            string? globalId = null,
            bool undoable = false,
            int attempt = 1) =>
            new ChangeRecord(
                step, attempt, nodeId, action,
                new ChangeTarget(kind, path, guid, globalId), undoable);

        [Test]
        public void TwoObjectsOfOnePathWithDifferentGlobalIdsAreBothListed()
        {
            var set = NewSet();

            Assert.IsTrue(
                set.TryAdd(Record(
                    0, "two", ChangeAction.Modified, "gameObject", ScenePath,
                    globalId: FirstGlobalId)),
                "the first object must be listed");
            Assert.IsTrue(
                set.TryAdd(Record(
                    0, "two", ChangeAction.Modified, "gameObject", ScenePath,
                    globalId: SecondGlobalId)),
                "a second object of the same path is a different target, not a repeat");
            Assert.AreEqual(2, set.Records.Count);
            Assert.AreEqual(
                FirstGlobalId, set.Records[0].Target.GlobalId,
                "the first element keeps the identifier it was listed with");
            Assert.AreEqual(SecondGlobalId, set.Records[1].Target.GlobalId);
        }

        [Test]
        public void TwoAssetsOfOnePathWithDifferentGuidsAreBothListed()
        {
            var set = NewSet();

            Assert.IsTrue(set.TryAdd(Record(
                0, "step", ChangeAction.Modified, "asset", ScenePath, guid: FirstGuid)));
            Assert.IsTrue(set.TryAdd(Record(
                0, "step", ChangeAction.Modified, "asset", ScenePath, guid: SecondGuid)));
            Assert.AreEqual(2, set.Records.Count);
        }

        [Test]
        public void AnAutomaticReportAndTheExplicitRecordOfOneTargetAreOneElement()
        {
            var set = NewSet();

            // The import callback can fire before the AssetDatabase answers for a
            // freshly created path, so the automatic element arrives identifier-less.
            Assert.IsTrue(set.TryAdd(Record(
                0, "step", ChangeAction.Created, "asset", ScenePath)));
            Assert.IsFalse(
                set.TryAdd(Record(
                    0, "step", ChangeAction.Created, "asset", ScenePath, guid: FirstGuid)),
                "the Operation's own record of the same creation is one fact, not a second target");
            Assert.AreEqual(1, set.Records.Count);
            Assert.AreEqual(
                FirstGuid, set.Records[0].Target.Guid,
                "the durable identifier only the repeat carries fills in");
        }

        [Test]
        public void AnIdentifierLessReportMergesIntoTheElementThatHasOne()
        {
            var set = NewSet();

            Assert.IsTrue(set.TryAdd(Record(
                0, "step", ChangeAction.Created, "asset", ScenePath, guid: FirstGuid)));
            Assert.IsFalse(
                set.TryAdd(Record(0, "step", ChangeAction.Created, "asset", ScenePath)),
                "a report that resolved nothing separates nothing");
            Assert.AreEqual(1, set.Records.Count);
            Assert.AreEqual(FirstGuid, set.Records[0].Target.Guid);
        }

        [Test]
        public void OnceOneTargetIsListedASecondTargetOfTheSamePathIsStillListed()
        {
            var set = NewSet();

            // The order the other tests do not cover: the identifier-less automatic
            // report of the first object is already merged with its guid, so the
            // second object's own guid must not be swallowed by that element.
            Assert.IsTrue(set.TryAdd(Record(
                0, "step", ChangeAction.Modified, "gameObject", ScenePath)));
            Assert.IsFalse(set.TryAdd(Record(
                0, "step", ChangeAction.Modified, "gameObject", ScenePath,
                globalId: FirstGlobalId)));
            Assert.IsTrue(set.TryAdd(Record(
                0, "step", ChangeAction.Modified, "gameObject", ScenePath,
                globalId: SecondGlobalId)));
            Assert.AreEqual(2, set.Records.Count);
            Assert.AreEqual(FirstGlobalId, set.Records[0].Target.GlobalId);
            Assert.AreEqual(SecondGlobalId, set.Records[1].Target.GlobalId);
        }

        [Test]
        public void DifferentStepsActionsAndTargetsStaySeparateElements()
        {
            var set = NewSet();

            Assert.IsTrue(set.TryAdd(Record(
                0, "a", ChangeAction.Modified, "asset", ScenePath, guid: FirstGuid)));
            Assert.IsTrue(set.TryAdd(Record(
                1, "a", ChangeAction.Modified, "asset", ScenePath, guid: FirstGuid)),
                "the same target touched by two steps stays two elements");
            Assert.IsTrue(set.TryAdd(Record(
                0, "a", ChangeAction.Deleted, "asset", ScenePath, guid: FirstGuid)),
                "one target changed then removed is two actions");
            Assert.IsTrue(set.TryAdd(Record(
                0, "a", ChangeAction.Modified, "gameObject", ScenePath, guid: FirstGuid)),
                "an artifact of another kind is not the same target");
            Assert.IsFalse(set.TryAdd(Record(
                0, "a", ChangeAction.Modified, "asset", ScenePath, guid: FirstGuid)),
                "the same element reported twice stays one");
            Assert.AreEqual(4, set.Records.Count);
        }

        [Test]
        public void TheSameElementOfTwoAttemptsIsTwoElements()
        {
            var set = NewSet();

            Assert.IsTrue(set.TryAdd(Record(
                0, "a", ChangeAction.Modified, "asset", ScenePath,
                guid: FirstGuid, attempt: 1)));
            Assert.IsTrue(
                set.TryAdd(Record(
                    0, "a", ChangeAction.Modified, "asset", ScenePath,
                    guid: FirstGuid, attempt: 2)),
                "SPEC §F7 keeps the attempts apart: the same action on the " +
                "same target in the retried attempt is its own element");
            Assert.AreEqual(2, set.Records.Count);
            Assert.AreEqual(1, set.Records[0].Attempt);
            Assert.AreEqual(2, set.Records[1].Attempt);
        }

        // The reconstruction path: an attempt's durable list is the writer's
        // already-deduplicated truth, so replaying it must keep every element
        // it lists. The live-report rule that lets an unresolved report merge
        // into a listed element cannot run backwards — a listed element's
        // missing identifier is final, and it must not absorb the identified
        // element a second object journaled behind it.
        [Test]
        public void AJournaledIdentifierLessElementDoesNotAbsorbAnIdentifiedOne()
        {
            var set = NewSet();

            Assert.IsTrue(set.Restore(Record(
                0, "two", ChangeAction.Modified, "gameObject", ScenePath)));
            Assert.IsTrue(
                set.Restore(Record(
                    0, "two", ChangeAction.Modified, "gameObject", ScenePath,
                    globalId: SecondGlobalId)),
                "the journaled second object of one path is its own element, " +
                "not absorbed by the identifier-less first one");
            Assert.AreEqual(2, set.Records.Count);
            Assert.IsNull(set.Records[0].Target.GlobalId);
            Assert.AreEqual(SecondGlobalId, set.Records[1].Target.GlobalId);

            Assert.IsFalse(
                set.Restore(Record(
                    0, "two", ChangeAction.Modified, "gameObject", ScenePath,
                    globalId: SecondGlobalId)),
                "a journaled element identical to the listed one is still the same fact");
            Assert.AreEqual(2, set.Records.Count);
        }

        [Test]
        public void AJournaledRepeatThatResolvedNothingStillMergesIntoTheListedElement()
        {
            var set = NewSet();

            Assert.IsTrue(set.Restore(Record(
                0, "step", ChangeAction.Created, "asset", ScenePath, guid: FirstGuid)));
            Assert.IsFalse(
                set.Restore(Record(0, "step", ChangeAction.Created, "asset", ScenePath)),
                "a journaled repeat that resolved nothing separates nothing");
            Assert.AreEqual(1, set.Records.Count);
            Assert.AreEqual(FirstGuid, set.Records[0].Target.Guid);
        }

        // The attempt recorder's half of the same merge: when the Run list's
        // deduplication enriches its listed element with an identifier only a
        // later report carried, the recorder's copy of that element must be
        // replaced — never appended to — so the attempt's journaled durable
        // list carries the identifier too.
        [Test]
        public void AnEnrichedListedElementReplacesTheRecordersOwnCopy()
        {
            var recorder = new StepChangeRecorder(0, "step");
            recorder.Modified(new ChangeTarget("gameObject", ScenePath), undoable: false);

            var listed = new ChangeRecord(
                0, 1, "step", ChangeAction.Modified,
                new ChangeTarget("gameObject", ScenePath, null, FirstGlobalId),
                false);
            recorder.MergeIdentifiers(listed);

            Assert.AreEqual(
                1, recorder.Records.Count,
                "the enrichment replaces the element, never adds a second one");
            Assert.AreSame(
                listed, recorder.Records[0],
                "the recorder's element is the enriched one the Run list carries");
        }

        [Test]
        public void AFinalizedListAcceptsNoFurtherElement()
        {
            var set = NewSet();
            Assert.IsTrue(set.TryAdd(Record(
                0, "a", ChangeAction.Modified, "asset", ScenePath, guid: FirstGuid)));
            set.MarkFinalized();

            Assert.IsFalse(
                set.TryAdd(Record(
                    0, "a", ChangeAction.Modified, "asset", ScenePath,
                    guid: SecondGuid)),
                "§F7 determines the list at Run end: a later report cannot extend it");
            Assert.AreEqual(1, set.Records.Count);
        }
    }
}
