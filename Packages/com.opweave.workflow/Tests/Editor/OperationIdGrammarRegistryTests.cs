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
                        "OperationIdGrammarRegistryTests.WaitForQuietEditor " +
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
                            "OperationIdGrammarRegistryTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "OperationIdGrammarRegistryTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // SPEC §A4 + §C3 rule 3: registration never admits a grammar-invalid
        // id, so every id the live Registry publishes matches the grammar —
        // including the test-support probes renamed by this ticket.
        [UnityTest]
        public IEnumerator EveryRegisteredOperationIdSatisfiesTheGrammar()
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
            foreach (var id in new[] { "test.contract.no_poll", "test.contract.bad_effect" })
            {
                Assert.IsNull(snapshot.Find(id, 1),
                    $"the intentionally broken probe '{id}' must not register");
                var kept = false;
                foreach (var failure in snapshot.Failures)
                {
                    if (failure.OperationId == id &&
                        failure.Diagnostics.Any(d => d.Code == ErrorCodes.OW_OP_003))
                    {
                        kept = true;
                        break;
                    }
                }
                Assert.IsTrue(kept,
                    $"the probe '{id}' must keep its OW-OP-003 rejection");
            }
        }
    }
}
