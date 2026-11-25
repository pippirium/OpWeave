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
// probe deletion still has a script compile in flight — or queued behind
// the lock but not yet started, where isCompiling still reads false and an
// isCompiling-only wait exits early into the same stale read.
// WaitForQuietEditor refreshes, crosses a proven pending compile or armed
// reload through the bounded DrainedScriptReload instruction and
// bounded-waits a sustained quiet window so a queued compile surfaces
// inside the wait, never behind it; it never releases the runner's
// assembly-reload lock on an idle Editor the way
// `yield return new RecompileScripts(false, false)` did — that
// unconditional unlock left the lock released, so a later fixture's reload
// fired inline instead of surfacing as pending.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        private const double WaitTimeoutSec = 240.0;
        private const double QuietSettleSec = 5.0;

        // The pending-request flag WaitForDomainReload.Perform polls —
        // Unity exposes it only to its own test framework
        // (InternalEditorUtility is an internal class), so the quiet
        // drain reaches it through reflection (the At01AgentStepsTests
        // probe, T-W2.4-07). A reload already armed behind the runner's
        // reload lock surfaces here while isCompiling is false — the
        // deferred state an isCompiling-only check misses.
        private const string ScriptReloadProbeType =
            "UnityEditorInternal.InternalEditorUtility";
        private const string ScriptReloadProbeMethod =
            "IsScriptReloadRequested";

        private static readonly MethodInfo? s_scriptReloadRequested =
            typeof(EditorApplication).Assembly
                .GetType(ScriptReloadProbeType)
                ?.GetMethod(
                    ScriptReloadProbeMethod,
                    BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null);

        // Non-null and shaped as OneTimeSetUp proved — the fixture does
        // not run at all on a Unity version whose probe is absent.
        private static bool ScriptReloadRequested() =>
            (bool)s_scriptReloadRequested!.Invoke(null, null)!;

        // The state a queued compile/reload reports while the runner's
        // lock holds it: a running script compile always ends in a reload
        // queued behind the lock, and a requested reload is already
        // pending — either is one the drain must cross, never ride into
        // the registry build below.
        private static bool ReloadPending() =>
            ScriptReloadRequested() || EditorApplication.isCompiling;

        // The pending probe the quiet drain trusts is only as strong as
        // its shape: prove it once up front (the At01AgentStepsTests
        // pattern, T-W2.4-07) so the fixture fails here — before any
        // test — instead of silently degrading to an isCompiling-only
        // check that misses a requested reload armed behind the lock.
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            var probe = s_scriptReloadRequested;
            if (probe is null ||
                !probe.IsStatic ||
                probe.GetParameters().Length != 0 ||
                probe.ReturnType != typeof(bool))
            {
                Assert.Fail(
                    "the domain-reload pending probe is unavailable on " +
                    "this Unity version — the quiet drain cannot prove a " +
                    "deferred reload is drained");
            }
        }

        // These tests never write a script file, so the only work they
        // can meet here is a leftover handed over by an earlier fixture:
        // a script compile still in flight or queued behind the runner's
        // lock, or the asset import in front of one. The sanctioned
        // bounded conditional drain (the At01AgentStepsTests
        // DrainEditorState idiom, T-W2.4-07) replaces the unconditional
        // `yield return new RecompileScripts(false, false)` residual:
        // that instruction's Perform requests a compile and unlocks the
        // runner's reload lock even when nothing is queued — and with
        // ExpectDomainReload=false UTF keeps no resume state, so a
        // still-pending predecessor reload crossing the unlock abandons
        // the test on cleared locals. Here a pending compile or
        // requested reload is crossed only at DrainedScriptReload — the
        // bounded ExpectDomainReload boundary the caller's relay hands
        // to UTF at the test body's own top level, where the captured PC
        // is correct — and everything else settles on bounded plain
        // frames: a queued compile surfaces as isCompiling a frame or
        // two after Refresh returns, so the wait only trusts a sustained
        // quiet spell and a periodic Refresh flushes the import queue so
        // a queued compile surfaces inside the window, never behind it.
        private static IEnumerator WaitForQuietEditor()
        {
            AssetDatabase.Refresh();
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            var nextRefreshAt = double.NegativeInfinity;
            var quietAt = -1.0;
            for (;;)
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "SearchServiceLiveRegistryTests.WaitForQuietEditor " +
                        "timed out after " + WaitTimeoutSec +
                        " s draining the armed editor state " +
                        "(scriptReloadRequested: " + ScriptReloadRequested() +
                        ", isCompiling: " + EditorApplication.isCompiling +
                        ", isUpdating: " + EditorApplication.isUpdating + ")");
                }
                if (ReloadPending())
                {
                    // A running or requested compile ends in a reload
                    // queued behind the runner's lock — cross it at the
                    // bounded sanctioned boundary, never ride it into
                    // the registry build.
                    quietAt = -1.0;
                    yield return new DrainedScriptReload(
                        "WaitForQuietEditor", WaitTimeoutSec);
                    continue;
                }
                if (EditorApplication.isUpdating)
                {
                    // An import still in flight may surface a compile —
                    // no quiet counts while it runs.
                    quietAt = -1.0;
                }
                else if (quietAt < 0.0)
                {
                    quietAt = EditorApplication.timeSinceStartup;
                }
                else if (EditorApplication.timeSinceStartup - quietAt >=
                         QuietSettleSec)
                {
                    break;
                }
                if (EditorApplication.timeSinceStartup >= nextRefreshAt)
                {
                    AssetDatabase.Refresh();
                    nextRefreshAt = EditorApplication.timeSinceStartup + 1.0;
                }
                yield return null;
            }
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the live registry cannot build over a failed script compile");
            Assert.IsFalse(EditorApplication.isCompiling,
                "the live registry cannot build while a compile is in flight");
            Assert.IsFalse(ScriptReloadRequested(),
                "the live registry cannot build while a script reload is pending");
        }

        // The bounded reload-crossing instruction — the
        // WaitForDomainReload.Perform wait with a deadline, run by UTF
        // through the IEditModeTestYieldInstruction interface so
        // ExpectDomainReload arms the runner's reload bookkeeping
        // exactly as with UTF's own instructions. It is yielded only
        // after ReloadPending() proved a real reload is armed, so the
        // unlock below always meets a queued reload and can never strand
        // the lock released on an idle Editor — the failure mode of the
        // unconditional RecompileScripts(false, false) unlock it
        // replaces. The one path where no reload can follow — a failed
        // compile — re-locks before failing.
        private sealed class DrainedScriptReload : IEditModeTestYieldInstruction
        {
            private readonly string m_phase;
            private readonly double m_timeoutSec;

            internal DrainedScriptReload(string phase, double timeoutSec)
            {
                m_phase = phase;
                m_timeoutSec = timeoutSec;
            }

            public bool ExpectDomainReload => true;
            public bool ExpectedPlaymodeState => false;

            public IEnumerator Perform()
            {
                // UTF yields the same first frame: the runner must see
                // the yielded instruction and arm its expected-domain-
                // reload bookkeeping before the reload can start.
                yield return null;

                var deadline =
                    EditorApplication.timeSinceStartup + m_timeoutSec;

                AssetDatabase.Refresh();
                EditorApplication.UnlockReloadAssemblies();

                // Wait out the armed state as WaitForDomainReload.Perform
                // does — while a script reload stays requested or a
                // compile runs — but bounded: the reload itself kills
                // the domain mid-wait, so a deadline expiry can only
                // mean the proven pending state never crossed.
                while (ReloadPending())
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        EditorApplication.LockReloadAssemblies();
                        Assert.Fail(
                            "SearchServiceLiveRegistryTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "SearchServiceLiveRegistryTests." + m_phase +
                        ": script compilation failed");
                }
            }
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
            // UTF resumes a reload-crossed [UnityTest] enumerator at its
            // yield with every local reset — a null waiter is that
            // resume, not a fresh start: rebuild the idempotent wait
            // and relay on instead of touching the dead reference (the
            // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07).
            IEnumerator? waiter = null;
            for (;;)
            {
                waiter ??= WaitForQuietEditor();
                if (!waiter.MoveNext())
                {
                    break;
                }
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
            // UTF resumes a reload-crossed [UnityTest] enumerator at its
            // yield with every local reset — a null waiter is that
            // resume, not a fresh start: rebuild the idempotent wait
            // and relay on instead of touching the dead reference (the
            // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07).
            IEnumerator? waiter = null;
            for (;;)
            {
                waiter ??= WaitForQuietEditor();
                if (!waiter.MoveNext())
                {
                    break;
                }
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
            // UTF resumes a reload-crossed [UnityTest] enumerator at its
            // yield with every local reset — a null waiter is that
            // resume, not a fresh start: rebuild the idempotent wait
            // and relay on instead of touching the dead reference (the
            // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07).
            IEnumerator? waiter = null;
            for (;;)
            {
                waiter ??= WaitForQuietEditor();
                if (!waiter.MoveNext())
                {
                    break;
                }
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
            // UTF resumes a reload-crossed [UnityTest] enumerator at its
            // yield with every local reset — a null waiter is that
            // resume, not a fresh start: rebuild the idempotent wait
            // and relay on instead of touching the dead reference (the
            // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07).
            IEnumerator? waiter = null;
            for (;;)
            {
                waiter ??= WaitForQuietEditor();
                if (!waiter.MoveNext())
                {
                    break;
                }
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
            // UTF resumes a reload-crossed [UnityTest] enumerator at its
            // yield with every local reset — a null waiter is that
            // resume, not a fresh start: rebuild the idempotent wait
            // and relay on instead of touching the dead reference (the
            // At01AgentStepsTests DrainEditorState idiom, T-W2.4-07).
            IEnumerator? waiter = null;
            for (;;)
            {
                waiter ??= WaitForQuietEditor();
                if (!waiter.MoveNext())
                {
                    break;
                }
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
