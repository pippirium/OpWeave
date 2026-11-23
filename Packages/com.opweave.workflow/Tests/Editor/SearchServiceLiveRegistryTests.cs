#nullable enable

// T-W3.1-02 [Edit] tests: local:T-W3.1-02:search-services — the SPEC §C6/§C7
// OperationSearchService called directly over the live Registry (production
// type + operation stages, so the standard Operations and the AT-01
// proj.transform.reset custom Operation are the real registered ones). No
// Agent API sits between the test and the service; the wire encoding and
// cursor belong to a later ticket.
//
// These are [UnityTest]s for the same reason SearchLiveRegistryTests is:
// the production §C3 stage-1 seam reads the live Editor compile state, and a
// synchronous [Test] can build over a stale stage 1 while a cross-fixture
// probe deletion still has a script compile in flight. `yield return new
// RecompileScripts(false, false)` unlocks the assembly-reload lock, drives
// the pending import and resumes once compiling stops.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Registry;
using OpWeave.Core.Search;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class SearchServiceLiveRegistryTests
    {
        private const string ResetOp = "proj.transform.reset";
        private const string ComponentAdd = "unity.component.add";
        private const string ApplyOverrides = "unity.prefab.apply_overrides";
        private const string Instantiate = "unity.prefab.instantiate";
        private const string GameObjectCreate = "unity.gameobject.create";

        private static RegistrySnapshot LiveSnapshot()
        {
            var build = RegistryBuildHost.Rebuild(
                RegistryTypeStage.Production(AutoExtensionExclusion.Instance),
                RegistryOperationStage.Production(),
                new RegistryPublication());
            Assert.IsFalse(build.Stale, "the live registry build must not be stale");
            Assert.IsNotNull(build.Snapshot, "the live registry must publish a snapshot");
            return build.Snapshot!;
        }

        private static IEnumerator WaitForQuietEditor()
        {
            yield return new RecompileScripts(false, false);
            var guard = 0;
            while (EditorApplication.isCompiling && guard++ < 600)
            {
                yield return null;
            }
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the live registry cannot build over a failed script compile");
            Assert.IsFalse(EditorApplication.isCompiling,
                "the live registry cannot build while a compile is in flight");
        }

        private static OperationSearchPage Search(
            RegistrySnapshot snapshot, OperationSearchRequest request)
        {
            var result = new OperationSearchService(snapshot).SearchOperations(request);
            Assert.IsTrue(result.Ok,
                "SearchOperations failed: " + string.Join(
                    "; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            return result.Value!;
        }

        // ── SPEC §C6 over the live Registry ───────────────────────────────

        // SPEC's draft-5.3 example (query "Animator", acceptsType
        // GameObjectRef, allowedEffects scene.modify) enumerates the standard
        // catalog, where unity.component.add leads — the [Core] test pins that
        // verbatim. Over the live Registry the AT-01 custom Operation accepts
        // GameObjectRef exactly as well, ties at the identical 2.0 score and
        // sorts first by the §C6 item-3 ID-Ordinal tie-break
        // ("proj.transform.reset" < "unity.*"): the deterministic ordering the
        // same query and Registry must reproduce every time.
        [UnityTest]
        public IEnumerator Draft53QueryOverLiveRegistryIsDeterministic()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var snapshot = LiveSnapshot();
            Assert.IsNotNull(snapshot.Find(ResetOp, 1),
                "the live Registry must contain the AT-01 Operation");

            var request = new OperationSearchRequest
            {
                Query = "Animator",
                AcceptsType = "unity.GameObjectRef",
                AllowedEffects = new[] { "scene.modify" },
            };
            var service = new OperationSearchService(snapshot);
            var first = service.SearchOperations(request).Value!;
            var second = service.SearchOperations(request).Value!;

            Assert.That(first.Items[0].Operation.Descriptor.Id, Is.EqualTo(ResetOp));
            Assert.That(first.Items[0].Operation.Origin, Is.EqualTo(OperationOrigin.Custom));
            Assert.That(first.Items[0].Score, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(first.Items[1].Operation.Descriptor.Id, Is.EqualTo(ComponentAdd),
                "unity.component.add leads the standard Operations at the same tied score");
            Assert.That(first.Items[1].Score, Is.EqualTo(2.0).Within(1e-9));
            Assert.That(
                second.Items.Select(i => i.Operation.Descriptor.Id),
                Is.EqualTo(first.Items.Select(i => i.Operation.Descriptor.Id)),
                "the same input and Registry must answer in identical order");
        }

        [UnityTest]
        public IEnumerator OriginCustomReturnsOnlyCustomOperations()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var snapshot = LiveSnapshot();
            var page = Search(snapshot, new OperationSearchRequest
            {
                Origin = OperationOrigin.Custom,
                Limit = 50,
            });

            Assert.That(page.Items.Count, Is.GreaterThan(0),
                "the live Registry registers at least the AT-01 custom Operation");
            Assert.IsTrue(page.Items.All(i => i.Operation.Origin == OperationOrigin.Custom),
                "origin=custom must return only custom Operations");
            Assert.IsTrue(page.Items.Any(i => i.Operation.Descriptor.Id == ResetOp));
        }

        [UnityTest]
        public IEnumerator ExcludedCandidatesAreNeverSearchable()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var snapshot = LiveSnapshot();
            var page = Search(snapshot, new OperationSearchRequest { Limit = 50 });
            var resultIds = page.Items.Select(i => i.Operation.Descriptor.Id).ToList();

            foreach (var exclusion in snapshot.Exclusions)
            {
                if (exclusion.Kind != RegistryCandidateKind.Operation)
                {
                    continue;
                }
                Assert.IsFalse(resultIds.Contains(exclusion.CandidateId),
                    "whitelist/registry-excluded candidate " + exclusion.CandidateId +
                    " must never be a search result");
                Assert.IsNull(snapshot.FindLatest(exclusion.CandidateId),
                    "an excluded candidate must not be registered");
            }
        }

        // ── SPEC §C7 over the live Registry ───────────────────────────────

        [UnityTest]
        public IEnumerator PrefabInstantiateInstanceDownstreamContainsApplyAndComponentAdd()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var snapshot = LiveSnapshot();
            var result = new OperationSearchService(snapshot).SearchCompatibleOperations(
                CompatibleOperationsRequest.ForOperationPort(
                    Instantiate, "instance", CompatibleDirection.Downstream));
            Assert.IsTrue(result.Ok,
                "SearchCompatibleOperations failed: " + string.Join(
                    "; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));
            var matches = result.Value!;

            var apply = matches.Single(m =>
                m.Operation.Descriptor.Id == ApplyOverrides);
            Assert.That(apply.Port, Is.EqualTo("instance"));
            Assert.That(apply.Match, Is.EqualTo(CompatibleMatchKind.Exact));
            var add = matches.Single(m =>
                m.Operation.Descriptor.Id == ComponentAdd);
            Assert.That(add.Port, Is.EqualTo("target"));
            Assert.That(add.Match, Is.EqualTo(CompatibleMatchKind.Base));
        }

        [UnityTest]
        public IEnumerator GameObjectCreateDownstreamIncludesTheCustomOperation()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var snapshot = LiveSnapshot();
            var result = new OperationSearchService(snapshot).SearchCompatibleOperations(
                CompatibleOperationsRequest.ForOperationPort(
                    GameObjectCreate, "gameObject", CompatibleDirection.Downstream));
            Assert.IsTrue(result.Ok,
                "SearchCompatibleOperations failed: " + string.Join(
                    "; ", result.Diagnostics.Select(d => d.Code + " " + d.Message)));

            var reset = result.Value!.Single(m =>
                m.Operation.Descriptor.Id == ResetOp);
            Assert.That(reset.Port, Is.EqualTo("target"));
            Assert.That(reset.Match, Is.EqualTo(CompatibleMatchKind.Exact));
            Assert.That(reset.Operation.Origin, Is.EqualTo(OperationOrigin.Custom),
                "the AT-01 custom Operation connects like a standard one (SPEC §M.7)");
        }
    }
}
