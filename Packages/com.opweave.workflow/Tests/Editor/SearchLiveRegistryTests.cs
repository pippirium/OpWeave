#nullable enable

// T-W3.1-01 [Edit] tests: local:T-W3.1-01:search-live-l11 — the SPEC §C5
// queries and the SPEC §T.4 L-11 30-query Japanese measurement against the
// live Registry (production type + operation stages, so the standard
// Operations and the AT-01 proj.transform.reset custom Operation are the
// real registered ones). The L-11 rate is evidence only: SPEC defines no
// threshold, so the test asserts no floor and records the measured rate on
// the test output for the implementation PR.
//
// These are [UnityTest]s, not [Test]s: the production §C3 stage-1 seam the
// build consults is the live Editor compile state
// (EditorApplication.isCompiling / EditorUtility.scriptCompilationFailed),
// and suite ordering can run this fixture behind fixtures whose synchronous
// TearDown deletes probe .cs assets with a trailing AssetDatabase.Refresh —
// leaving a script compile in flight, or queued behind the lock but not yet
// started, where isCompiling still reads false and an isCompiling-only wait
// exits early into the same stale read. That compile's domain reload is
// held at the EditMode runner's assembly-reload lock while a test runs: a
// test that only polls the pipeline flags across frame yields can never
// observe a quiet Editor (the pipeline drains only after the lock is
// released), and a synchronous [Test] reads a stale stage 1 instead.
// WaitForQuietEditor refreshes, crosses a proven pending compile or armed
// reload through the bounded DrainedScriptReload instruction and
// bounded-waits a sustained quiet window so a queued compile surfaces
// inside the wait, never behind it; it never releases the runner's
// assembly-reload lock on an idle Editor the way
// `yield return new RecompileScripts(false, false)` did — that
// unconditional unlock left the lock released, so a later fixture's reload
// fired inline instead of surfacing as pending. The stale check itself is
// not weakened: the production rebuild still must not be stale, and
// scriptCompilationFailed keeps its own assert.
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
    internal sealed class SearchLiveRegistryTests
    {
        private const string PrefabOp = "unity.prefab.create_from_gameobject";
        private const string ResetOp = "proj.transform.reset";

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

        private static List<string> Ids(IReadOnlyList<OperationSearchHit> hits) =>
            hits.Select(h => h.Operation.Descriptor.Id).ToList();

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
                        "SearchLiveRegistryTests.WaitForQuietEditor timed " +
                        "out after " + WaitTimeoutSec +
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
                            "SearchLiveRegistryTests." + m_phase +
                            " timed out after " + m_timeoutSec +
                            " s: the proven pending reload never crossed");
                    }
                    yield return null;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    EditorApplication.LockReloadAssemblies();
                    Assert.Fail(
                        "SearchLiveRegistryTests." + m_phase +
                        ": script compilation failed");
                }
            }
        }

        // ── SPEC §C5 queries against the live Registry ────────────────────

        [UnityTest]
        public IEnumerator C5QueriesRankExpectedOperationsOverLiveRegistry()
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
            var index = snapshot.SearchIndex;

            var prefab = index.Search("プレハブ 作成");
            Assert.That(Ids(prefab).IndexOf(PrefabOp),
                Is.GreaterThanOrEqualTo(0).And.LessThan(3),
                "「プレハブ 作成」 must rank " + PrefabOp + " in the top 3, got " +
                string.Join(", ", Ids(prefab).Take(6)));

            foreach (var query in new[] { "transform reset", "トランスフォーム リセット" })
            {
                var hits = index.Search(query);
                Assert.That(hits.Count, Is.GreaterThan(0), "no hits for " + query);
                Assert.That(hits[0].Operation.Descriptor.Id, Is.EqualTo(ResetOp),
                    query + " must rank the AT-01 Operation first, got " +
                    string.Join(", ", Ids(hits).Take(4)));
                Assert.That(hits[0].Operation.Origin,
                    Is.EqualTo(OperationOrigin.Custom));
            }
        }

        // ── SPEC §T.4 L-11: top-3 hit rate over 30 Japanese queries ───────
        //
        // The query set is a local, reversible test fixture (SPEC fixes only
        // "30 Japanese queries over the standard Operations and the sample
        // Operation"): 29 queries target standard Operations across every
        // catalog area, one targets AT-01. Each entry maps a natural
        // Japanese phrasing to its intended Operation so the top-3 rate is
        // measurable; there is no threshold to meet.

        private static readonly (string Query, string Expected)[] L11Queries =
        {
            ("シーンを作る", "unity.scene.create"),
            ("新しいシーン", "unity.scene.create"),
            ("シーンを開く", "unity.scene.open"),
            ("シーンを保存", "unity.scene.save"),
            ("シーンを閉じる", "unity.scene.close"),
            ("ゲームオブジェクト作成", "unity.gameobject.create"),
            ("オブジェクトを探す", "unity.gameobject.find"),
            ("ゲームオブジェクトを削除", "unity.gameobject.delete"),
            ("親を設定する", "unity.gameobject.set_parent"),
            ("位置と回転を変える", "unity.transform.set"),
            ("コンポーネントを追加", "unity.component.add"),
            ("コンポーネントを外す", "unity.component.remove"),
            ("プレハブを配置", "unity.prefab.instantiate"),
            ("プレハブ 作成", "unity.prefab.create_from_gameobject"),
            ("プレハブのバリアントを作る", "unity.prefab.create_variant"),
            ("オーバーライドを適用", "unity.prefab.apply_overrides"),
            ("オーバーライドを取り消し", "unity.prefab.revert_overrides"),
            ("プレハブをアンパック", "unity.prefab.unpack"),
            ("プレハブの中身を編集", "unity.prefab.open_contents"),
            ("編集内容を保存", "unity.prefab.save_contents"),
            ("アセットを検索", "unity.asset.find"),
            ("フォルダを作る", "unity.asset.create_folder"),
            ("マテリアルを作る", "unity.asset.create_material"),
            ("アセットをコピー", "unity.asset.copy"),
            ("アセットを移動", "unity.asset.move"),
            ("アセットを消す", "unity.asset.delete"),
            ("ファイルを取り込む", "unity.asset.import"),
            ("スクリプトを生成", "unity.script.create_from_template"),
            ("プロパティを読む", "unity.property.get"),
            ("トランスフォーム リセット", "proj.transform.reset"),
        };

        [UnityTest]
        public IEnumerator L11MeasuresTop3HitRateOver30JapaneseQueries()
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

            var index = LiveSnapshot().SearchIndex;
            Assert.That(L11Queries.Length, Is.EqualTo(30), "L-11 fixes 30 queries");

            var hits3 = 0;
            foreach (var (query, expected) in L11Queries)
            {
                var top = index.Search(query, 3);
                var ids = Ids(top);
                var hit = ids.Contains(expected);
                if (hit)
                {
                    hits3++;
                }
                TestContext.Out.WriteLine(
                    (hit ? "L-11 HIT  " : "L-11 MISS ") + query +
                    " -> expect " + expected + " | top3: " +
                    string.Join(", ", ids.Select((id, i) => (i + 1) + "." + id)));
            }
            var rate = hits3 / 30.0;
            TestContext.Out.WriteLine(
                "L-11 Japanese bigram top-3 hit rate: " + hits3 + "/30 = " +
                rate.ToString("F3") + " (no SPEC threshold; evidence only)");
            UnityEngine.Debug.Log(
                "L-11 Japanese bigram top-3 hit rate: " + hits3 + "/30 = " +
                rate.ToString("F3"));
        }
    }
}
