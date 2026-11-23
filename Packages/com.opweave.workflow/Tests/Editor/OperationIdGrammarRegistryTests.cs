#nullable enable

// T-W1.5-05 [Edit] tests: local:T-W1.5-05:operation-id-grammar — the live
// Registry enumeration check of SPEC §A4 / §C3 rule 3 over the production
// build: no registered Operation id violates the grammar, every
// grammar-valid test-support probe still registers, and the probes that
// are intentionally invalid for another reason keep the same OW-OP-003
// rejection they had on main under their renamed, grammar-valid ids.
//
// [UnityTest] for the same reason SearchServiceLiveRegistryTests is: the
// production §C3 stage-1 seam reads the live Editor compile state, and a
// synchronous [Test] can build over a stale stage 1 while a cross-fixture
// probe deletion still has a script compile in flight. WaitForQuietEditor
// refreshes, crosses a proven in-flight compile's reload through the
// bounded ProvenScriptReload instruction and bounded-waits the remaining
// busy flags; it never releases the runner's assembly-reload lock on an
// idle Editor the way `yield return new RecompileScripts(false, false)`
// did — that unconditional unlock left the lock released, so a later
// fixture's reload fired inline instead of surfacing as pending.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Registry;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine.TestTools;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// local:T-W1.5-05:operation-id-grammar — the live-Registry check:
    /// enumerates every registered Operation and asserts the SPEC §A4
    /// Operation id grammar for each (acceptance criterion "no registered
    /// Operation id in the TestProject violates the grammar").
    /// </summary>
    internal sealed class OperationIdGrammarRegistryTests
    {
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

        private const string EditorBusyState =
            "EditorApplication.isCompiling || EditorApplication.isUpdating";

        // These tests never write a script file, so the only work they
        // can meet here is a leftover handed over by an earlier fixture:
        // a script compile still in flight, or the asset import in
        // front of one. A compile proven in flight ends in a domain
        // reload that must be crossed inside this wait — never
        // forwarded to the next fixture and never waited out into a
        // stale domain — so it yields ProvenScriptReload, the bounded
        // instruction below that unlocks only after that proof.
        // Everything else (isUpdating, residual compile flags) settles
        // in a bounded plain-frame wait that fails naming the state.
        // No path unlocks unconditionally the way
        // `RecompileScripts(false, false)` did: its Perform released the
        // runner's run-wide assembly-reload lock even when nothing was
        // queued, and with no reload following, the lock stayed
        // released — the failure this wait is corrected for.
        private static IEnumerator WaitForQuietEditor()
        {
            // Flush queued imports so a queued script compile surfaces
            // as isCompiling inside the wait instead of behind it.
            AssetDatabase.Refresh();
            var deadline = EditorApplication.timeSinceStartup + WaitTimeoutSec;
            while (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (EditorApplication.timeSinceStartup >= deadline)
                {
                    Assert.Fail(
                        "OperationIdGrammarRegistryTests.WaitForQuietEditor " +
                        "timed out after " + WaitTimeoutSec +
                        " s waiting for " + EditorBusyState + " to clear");
                }
                if (EditorApplication.isCompiling)
                {
                    yield return new ProvenScriptReload(WaitTimeoutSec);
                }
                yield return null;
            }
            Assert.IsFalse(EditorUtility.scriptCompilationFailed,
                "the live registry cannot build over a failed script compile");
            Assert.IsFalse(EditorApplication.isCompiling,
                "the live registry cannot build while a compile is in flight");
        }

        // RecompileScripts.Perform with a deadline — the one extension
        // point UTF opens for a bounded reload crossing from a test
        // body. It is yielded only with a script compile already proven
        // in flight, and under the runner's run-wide reload lock a
        // running or just-finished script compile always has its domain
        // reload queued behind the lock: the unlock below therefore
        // always meets a real pending reload and can never strand the
        // lock released on an idle Editor — the failure mode of the
        // unconditional RecompileScripts unlock it replaces. The one
        // path where no reload can follow, a failed compile, re-locks
        // before failing.
        private sealed class ProvenScriptReload : IEditModeTestYieldInstruction
        {
            private readonly double m_timeoutSec;

            internal ProvenScriptReload(double timeoutSec) =>
                m_timeoutSec = timeoutSec;

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

                while (EditorApplication.isCompiling)
                {
                    if (EditorApplication.timeSinceStartup >= deadline)
                    {
                        EditorApplication.LockReloadAssemblies();
                        Assert.Fail(
                            "OperationIdGrammarRegistryTests." +
                            "WaitForQuietEditor timed out after " +
                            m_timeoutSec +
                            " s: EditorApplication.isCompiling did " +
                            "not clear");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "OperationIdGrammarRegistryTests." +
                        "WaitForQuietEditor: script compilation failed");
                }
            }
        }

        // SPEC §A4 + §C3 rule 3: registration never admits a grammar-invalid
        // id, so every id the live Registry publishes matches the grammar —
        // including the test-support probes renamed by this ticket.
        [UnityTest]
        public IEnumerator EveryRegisteredOperationIdSatisfiesTheGrammar()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var snapshot = LiveSnapshot();
            Assert.Greater(snapshot.Operations.Count, 0,
                "the live Registry must register at least the standard Operations");

            var violating = snapshot.Operations
                .Where(operation => !IdGrammar.IsValidOperationId(operation.Descriptor.Id))
                .Select(operation => operation.Descriptor.Id)
                .ToList();
            Assert.That(violating, Is.Empty,
                "every registered Operation id must satisfy the SPEC §A4 grammar: " +
                string.Join(", ", violating));
        }

        // The renamed probes of this ticket: an id that violates no other
        // rule registers under its grammar-valid spelling (acceptance
        // criterion "every test-support probe that is otherwise valid
        // registers without OW-OP-003").
        [UnityTest]
        public IEnumerator RenamedProbeIdsRegister()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var snapshot = LiveSnapshot();
            foreach (var id in new[]
            {
                "test.contract.ensure_marker",
                "test.contract.marker_asset",
                "test.at02.weakened_create",
                "test.at02.weakened_prefab_create",
                "test.at02.weakened_asset_create",
            })
            {
                Assert.IsNotNull(snapshot.Find(id, 1),
                    $"the grammar-valid probe '{id}' must register (SPEC §A4)");
            }
        }

        // A probe that is intentionally invalid for another reason is still
        // rejected with the same expected diagnostic as on main — under its
        // grammar-valid id the waitable-without-Poll and unknown-side-effect
        // dummies keep their OW-OP-003 rejections (SPEC §C3 rule 3).
        [UnityTest]
        public IEnumerator IntentionallyBrokenProbesKeepTheirOwOp003Rejection()
        {
            var waiter = WaitForQuietEditor();
            while (waiter.MoveNext())
            {
                yield return waiter.Current;
            }

            var snapshot = LiveSnapshot();
            foreach (var id in new[] { "test.contract.no_poll", "test.contract.bad_effect" })
            {
                Assert.IsNull(snapshot.Find(id, 1),
                    $"the intentionally broken probe '{id}' must not register");
                Assert.IsTrue(
                    snapshot.Failures.Any(failure =>
                        failure.OperationId == id &&
                        failure.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_003)),
                    $"the probe '{id}' must keep its OW-OP-003 rejection");
            }
        }
    }
}
