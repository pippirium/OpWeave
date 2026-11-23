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
// leaving a script compile in flight. That compile's domain reload is held
// at the EditMode runner's assembly-reload lock while a test runs: a test
// that only polls the pipeline flags across frame yields can never observe
// a quiet Editor (the pipeline drains only after the lock is released), and
// a synchronous [Test] reads a stale stage 1 instead. `yield return new
// RecompileScripts(false, false)` — the same mechanism every
// compile-crossing fixture here uses — unlocks the reload, drives the
// pending import and resumes the iterator once compiling stops (in the
// fresh domain when a reload landed). The stale check itself is not
// weakened: the production rebuild still must not be stale, and
// scriptCompilationFailed keeps its own assert. Stage 1 never reads
// isUpdating, so it is deliberately not polled.
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

        // ── SPEC §C5 queries against the live Registry ────────────────────

        [UnityTest]
        public IEnumerator C5QueriesRankExpectedOperationsOverLiveRegistry()
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
